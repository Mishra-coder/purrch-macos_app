using Purrch.Core;

namespace Purrch.Features.Media;

// MARK: - DSP building blocks (audio thread only)

/// Xorshift noise source.
internal struct Rng
{
    private uint _state;

    public Rng(uint seed) => _state = seed == 0 ? 0x9E3779B9 : seed;

    /// Uniform in [-1, 1].
    public float White()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return unchecked((int)_state) / (float)int.MaxValue;
    }

    /// Uniform in [0, 1).
    public float Unit() => White() * 0.5f + 0.5f;
}

/// Paul Kellet's economical pink-noise filter.
internal struct Pink
{
    private float _b0, _b1, _b2, _b3, _b4, _b5, _b6;

    public float Next(float w)
    {
        _b0 = 0.99886f * _b0 + w * 0.0555179f;
        _b1 = 0.99332f * _b1 + w * 0.0750759f;
        _b2 = 0.96900f * _b2 + w * 0.1538520f;
        _b3 = 0.86650f * _b3 + w * 0.3104856f;
        _b4 = 0.55000f * _b4 + w * 0.5329522f;
        _b5 = -0.7616f * _b5 - w * 0.0168980f;
        var output = _b0 + _b1 + _b2 + _b3 + _b4 + _b5 + _b6 + w * 0.5362f;
        _b6 = w * 0.115926f;
        return output * 0.11f;
    }
}

/// Leaky-integrated (brown) noise.
internal struct Brown
{
    private float _last;

    public float Next(float w)
    {
        _last = (_last + 0.02f * w) / 1.02f;
        return _last * 3.5f;
    }
}

internal enum FilterKind { Lowpass, Highpass, Bandpass }

/// RBJ biquad.
internal struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2;
    private float _x1, _x2, _y1, _y2;

    public Biquad(FilterKind kind, float freq, float q, float sr)
    {
        _b0 = 1; _b1 = 0; _b2 = 0; _a1 = 0; _a2 = 0;
        _x1 = _x2 = _y1 = _y2 = 0;
        Set(kind, freq, q, sr);
    }

    public void Set(FilterKind kind, float freq, float q, float sr)
    {
        var f = Math.Min(Math.Max(freq, 10), sr * 0.45f);
        var w0 = 2 * MathF.PI * f / sr;
        var cosw = MathF.Cos(w0);
        var alpha = MathF.Sin(w0) / (2 * Math.Max(q, 0.05f));
        var a0 = 1 + alpha;
        switch (kind)
        {
            case FilterKind.Lowpass:
                _b0 = (1 - cosw) / 2; _b1 = 1 - cosw; _b2 = (1 - cosw) / 2;
                break;
            case FilterKind.Highpass:
                _b0 = (1 + cosw) / 2; _b1 = -(1 + cosw); _b2 = (1 + cosw) / 2;
                break;
            default:
                _b0 = alpha; _b1 = 0; _b2 = -alpha;
                break;
        }
        _a1 = -2 * cosw; _a2 = 1 - alpha;
        _b0 /= a0; _b1 /= a0; _b2 /= a0; _a1 /= a0; _a2 /= a0;
    }

    public float Process(float x)
    {
        var y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
        return y;
    }
}

/// A decaying sinusoidal "plink" (rain drops, bubbles).
internal struct Blip
{
    private float _env, _decay, _phase, _inc, _chirp;

    public void Trigger(float amp, float freq, float decaySeconds, float sr, float chirp = 1)
    {
        _env = amp;
        _decay = MathF.Exp(-1 / (decaySeconds * sr));
        _inc = 2 * MathF.PI * freq / sr;
        _chirp = chirp;
        _phase = 0;
    }

    public float Next()
    {
        if (_env <= 0.0001f) return 0;
        var v = MathF.Sin(_phase) * _env;
        _phase += _inc;
        if (_phase > 2 * MathF.PI) _phase -= 2 * MathF.PI;
        _inc *= _chirp;
        _env *= _decay;
        return v;
    }
}

// MARK: - Sounds

public enum AmbientSound { Rain, Storm, Wind, Ocean, Fire, Stream, Lofi, White, Pink, Brown }

public static class AmbientSoundInfo
{
    public static IReadOnlyList<AmbientSound> All { get; } = Enum.GetValues<AmbientSound>();

    public static string Title(this AmbientSound s) => s switch
    {
        AmbientSound.Rain => "Rain",
        AmbientSound.Storm => "Storm",
        AmbientSound.Wind => "Wind",
        AmbientSound.Ocean => "Ocean",
        AmbientSound.Fire => "Fireplace",
        AmbientSound.Stream => "Stream",
        AmbientSound.Lofi => "Lo-fi Hum",
        AmbientSound.White => "White Noise",
        AmbientSound.Pink => "Pink Noise",
        _ => "Brown Noise",
    };

    /// Name in ambience.json, the same as the Mac app's raw value.
    public static string Key(this AmbientSound s) => s.ToString().ToLowerInvariant();
}

/// Per-sound synthesizer. <see cref="TargetOn"/> and <see cref="Volume"/> are written on the UI
/// thread and read on the audio thread; everything else belongs to the audio thread.
internal sealed class Voice
{
    private static readonly float[][] Chords =
    {
        // Soft jazzy progression: Cmaj7 - Am7 - Fmaj7 - G6 (low register).
        new[] { 130.81f, 164.81f, 196.00f, 246.94f },
        new[] { 110.00f, 130.81f, 164.81f, 196.00f },
        new[] { 87.31f, 130.81f, 164.81f, 220.00f },
        new[] { 98.00f, 123.47f, 146.83f, 164.81f },
    };

    public readonly AmbientSound Sound;
    public volatile bool TargetOn;
    public volatile float Volume = 0.6f;

    private readonly float _sr;
    private readonly float _fadeStep;
    private float _fade;
    private float _smoothVol = 0.6f;
    private Rng _rng;
    private Pink _pink;
    private Brown _brown;
    private Brown _brown2;
    private Biquad _f1, _f2, _f3;
    private readonly Blip[] _blips = new Blip[8];
    private int _blipIndex;
    private double _t;
    private int _counter;
    private float _rumbleEnv, _rumbleTarget;
    private float _crackleEnv, _crackleDecay = 0.99f;
    // Lo-fi
    private readonly float[] _phases = new float[8];
    private readonly float[] _freqs = Enumerable.Repeat(110f, 8).ToArray();
    private readonly float[] _targetFreqs = Enumerable.Repeat(110f, 8).ToArray();
    private int _chordIndex;
    private double _chordTimer;

    public Voice(AmbientSound sound, int sampleRate)
    {
        Sound = sound;
        _sr = sampleRate;
        _fadeStep = 1f / sampleRate; // one second in or out
        _rng = new Rng((uint)HashCode.Combine(sound, Environment.TickCount) | 1);
        var sr = _sr;
        (_f1, _f2, _f3) = sound switch
        {
            AmbientSound.Rain => (new Biquad(FilterKind.Highpass, 500, 0.7f, sr), new Biquad(FilterKind.Lowpass, 7000, 0.7f, sr),
                new Biquad(FilterKind.Lowpass, 120, 0.7f, sr)),
            AmbientSound.Storm => (new Biquad(FilterKind.Highpass, 300, 0.7f, sr), new Biquad(FilterKind.Lowpass, 4500, 0.7f, sr),
                new Biquad(FilterKind.Lowpass, 90, 0.8f, sr)),
            AmbientSound.Wind => (new Biquad(FilterKind.Bandpass, 500, 1.2f, sr), new Biquad(FilterKind.Lowpass, 2500, 0.7f, sr),
                new Biquad(FilterKind.Bandpass, 1200, 4, sr)),
            AmbientSound.Ocean => (new Biquad(FilterKind.Lowpass, 800, 0.7f, sr), new Biquad(FilterKind.Highpass, 1500, 0.7f, sr),
                new Biquad(FilterKind.Lowpass, 6000, 0.7f, sr)),
            AmbientSound.Fire => (new Biquad(FilterKind.Lowpass, 180, 0.7f, sr), new Biquad(FilterKind.Highpass, 1200, 0.7f, sr),
                new Biquad(FilterKind.Bandpass, 600, 0.8f, sr)),
            AmbientSound.Stream => (new Biquad(FilterKind.Bandpass, 1800, 0.6f, sr), new Biquad(FilterKind.Highpass, 300, 0.7f, sr),
                new Biquad(FilterKind.Lowpass, 6000, 0.7f, sr)),
            AmbientSound.Lofi => (new Biquad(FilterKind.Lowpass, 1400, 0.7f, sr), new Biquad(FilterKind.Highpass, 2500, 0.7f, sr),
                new Biquad(FilterKind.Bandpass, 3000, 1, sr)),
            _ => (new Biquad(FilterKind.Lowpass, 16000, 0.7f, sr), new Biquad(FilterKind.Highpass, 25, 0.7f, sr),
                new Biquad(FilterKind.Lowpass, 16000, 0.7f, sr)),
        };
        if (sound == AmbientSound.Lofi) SetChord(0, immediate: true);
    }

    /// Faded all the way out and not asked to play.
    public bool IsSilent => !TargetOn && _fade <= 0;

    /// Adds this sound into the mix. Returns false (and adds nothing) while silent.
    public bool RenderInto(float[] mix)
    {
        var target = TargetOn ? 1f : 0f;
        if (_fade <= 0 && target == 0) return false;
        var volume = Volume;
        var dt = 1.0 / _sr;
        for (var i = 0; i < mix.Length; i++)
        {
            if (_fade < target) _fade = Math.Min(target, _fade + _fadeStep);
            else if (_fade > target) _fade = Math.Max(target, _fade - _fadeStep);
            _smoothVol += (volume - _smoothVol) * 0.0008f;
            // Equal-power-ish fade curve.
            mix[i] += Sample() * _fade * _fade * _smoothVol;
            _t += dt;
        }
        return true;
    }

    private int NextBlip()
    {
        _blipIndex = (_blipIndex + 1) % _blips.Length;
        return _blipIndex;
    }

    private float BlipSum()
    {
        var s = 0f;
        for (var i = 0; i < _blips.Length; i++) s += _blips[i].Next();
        return s;
    }

    private void SetChord(int index, bool immediate = false)
    {
        var chord = Chords[index % Chords.Length];
        for (var n = 0; n < 4; n++)
        {
            _targetFreqs[n * 2] = chord[n] * 0.997f;
            _targetFreqs[n * 2 + 1] = chord[n] * 1.003f;
            if (!immediate) continue;
            _freqs[n * 2] = _targetFreqs[n * 2];
            _freqs[n * 2 + 1] = _targetFreqs[n * 2 + 1];
        }
    }

    private static float Sin(double x) => (float)Math.Sin(x);

    private float Sample()
    {
        var w = _rng.White();
        _counter = unchecked(_counter + 1);
        var sr = _sr;
        var t = _t;
        switch (Sound)
        {
            case AmbientSound.White:
                return w * 0.25f;
            case AmbientSound.Pink:
                return _pink.Next(w) * 0.9f;
            case AmbientSound.Brown:
                return _f2.Process(_brown.Next(w)) * 0.8f;

            case AmbientSound.Rain:
            {
                var hiss = _f2.Process(_f1.Process(_pink.Next(w))) * 0.55f;
                var body = _f3.Process(_brown.Next(_rng.White())) * 0.25f;
                if (_rng.Unit() < 45 / sr)
                {
                    var i = NextBlip();
                    _blips[i].Trigger(0.04f + _rng.Unit() * 0.12f, 1800 + _rng.Unit() * 3500,
                        0.004f + _rng.Unit() * 0.012f, sr, chirp: 0.9998f);
                }
                return hiss + body + BlipSum();
            }

            case AmbientSound.Storm:
            {
                var swell = (float)(0.8 + 0.2 * Math.Sin(t * 2 * Math.PI * 0.07));
                var hiss = _f2.Process(_f1.Process(_pink.Next(w))) * 0.8f * swell;
                if (_rng.Unit() < 90 / sr)
                {
                    var i = NextBlip();
                    _blips[i].Trigger(0.05f + _rng.Unit() * 0.12f, 1200 + _rng.Unit() * 3000,
                        0.003f + _rng.Unit() * 0.01f, sr);
                }
                // Thunder: rare slow envelope over very low brown noise.
                if (_rumbleTarget == 0 && _rng.Unit() < 1 / (sr * 14)) _rumbleTarget = 0.6f + _rng.Unit() * 0.6f;
                if (_rumbleTarget > 0)
                {
                    _rumbleEnv += (_rumbleTarget - _rumbleEnv) * (1 / (sr * 0.6f));
                    if (_rumbleEnv >= _rumbleTarget * 0.97f) _rumbleTarget = 0;
                }
                else
                {
                    _rumbleEnv *= MathF.Exp(-1 / (sr * 2.5f));
                }
                var rumble = _f3.Process(_brown2.Next(_rng.White())) * 2.2f * _rumbleEnv;
                return hiss + BlipSum() + rumble;
            }

            case AmbientSound.Wind:
            {
                if (_counter % 64 == 0)
                {
                    var m = 0.5 + 0.3 * Math.Sin(t * 2 * Math.PI * 0.05) + 0.2 * Math.Sin(t * 2 * Math.PI * 0.13 + 1.7);
                    _f1.Set(FilterKind.Bandpass, (float)(250 + 650 * m), 1.1f, sr);
                    _f3.Set(FilterKind.Bandpass, (float)(900 + 900 * m), 6, sr);
                }
                var gust = (float)(0.45 + 0.35 * Math.Sin(t * 2 * Math.PI * 0.08) + 0.2 * Math.Sin(t * 2 * Math.PI * 0.21 + 0.5));
                var gustBase = _f2.Process(_f1.Process(w)) * 0.9f;
                var whistle = _f3.Process(w) * 0.12f * gust * gust;
                return (gustBase + whistle) * Math.Max(0.1f, gust);
            }

            case AmbientSound.Ocean:
            {
                // ~0.1 Hz swell, sharpened so waves crash then recede.
                var phase = (Math.Sin(t * 2 * Math.PI * 0.1) + 1) / 2;
                var swell = (float)Math.Pow(phase, 2.2);
                if (_counter % 64 == 0) _f1.Set(FilterKind.Lowpass, 300 + 1500 * swell, 0.7f, sr);
                var rumble = _f1.Process(_brown.Next(w)) * (0.25f + 0.75f * swell);
                var foam = _f3.Process(_f2.Process(_pink.Next(_rng.White()))) * 0.35f * swell * swell;
                return (rumble + foam) * 0.9f;
            }

            case AmbientSound.Fire:
            {
                var roar = _f1.Process(_brown.Next(w)) * 0.55f;
                var hiss = _f2.Process(w) * 0.015f * (0.6f + 0.4f * _rng.Unit());
                // Crackle: sparse noise pops with fast decay, occasionally in bursts.
                if (_rng.Unit() < 7 / sr || (_crackleEnv > 0.05f && _rng.Unit() < 120 / sr))
                {
                    _crackleEnv = 0.25f + _rng.Unit() * 0.75f;
                    _crackleDecay = MathF.Exp(-1 / (sr * (0.0006f + _rng.Unit() * 0.003f)));
                }
                var pop = _f3.Process(_rng.White()) * _crackleEnv * 1.6f;
                _crackleEnv *= _crackleDecay;
                return roar + hiss + pop;
            }

            case AmbientSound.Stream:
            {
                if (_counter % 64 == 0)
                {
                    var m = (float)(0.5 + 0.25 * Math.Sin(t * 2 * Math.PI * 3.1) * Math.Sin(t * 2 * Math.PI * 0.37)
                                    + 0.25 * Math.Sin(t * 2 * Math.PI * 5.3 + 1));
                    _f1.Set(FilterKind.Bandpass, 1200 + 1600 * m, 0.7f, sr);
                }
                var water = _f3.Process(_f2.Process(_f1.Process(_pink.Next(w)))) * 0.9f;
                if (_rng.Unit() < 14 / sr)
                {
                    var i = NextBlip();
                    _blips[i].Trigger(0.03f + _rng.Unit() * 0.06f, 500 + _rng.Unit() * 900,
                        0.015f + _rng.Unit() * 0.03f, sr, chirp: 1.00004f);
                }
                return water + BlipSum();
            }

            default: // Lo-fi
            {
                _chordTimer += 1.0 / sr;
                if (_chordTimer > 4)
                {
                    _chordTimer = 0;
                    _chordIndex++;
                    SetChord(_chordIndex);
                }
                var wow = 1 + 0.0025f * Sin(t * 2 * Math.PI * 0.5);
                var tone = 0f;
                for (var n = 0; n < 8; n++)
                {
                    _freqs[n] += (_targetFreqs[n] - _freqs[n]) * 0.00008f;
                    _phases[n] += 2 * MathF.PI * _freqs[n] * wow / sr;
                    if (_phases[n] > 2 * MathF.PI) _phases[n] -= 2 * MathF.PI;
                    // Sine with a touch of 2nd harmonic for warmth.
                    tone += MathF.Sin(_phases[n]) + 0.15f * MathF.Sin(_phases[n] * 2);
                }
                var breathe = 0.75f + 0.25f * Sin(t * 2 * Math.PI * 0.25);
                tone = _f1.Process(tone * 0.05f * breathe);
                var hiss = _f2.Process(_pink.Next(w)) * 0.03f;
                // Vinyl crackle.
                if (_rng.Unit() < 5 / sr) _crackleEnv = 0.1f + _rng.Unit() * 0.25f;
                var click = _f3.Process(_rng.White()) * _crackleEnv;
                _crackleEnv *= 0.993f;
                return tone + hiss + click;
            }
        }
    }
}

// MARK: - Engine

/// Layers the ten synthesized sounds and plays them through waveOut. The device opens with the first
/// sound and closes again once everything has faded out, so a silent Purrch uses no audio or CPU.
public sealed class AmbienceEngine
{
    private static AmbienceEngine? _shared;
    public static AmbienceEngine Shared => _shared ??= new();

    private const int SampleRate = 48_000;
    /// How long everything must be silent before the device closes.
    private const double IdleSeconds = 1.6;

    private sealed class Saved
    {
        public Dictionary<string, double> Volumes { get; set; } = new();
        public double Master { get; set; } = 0.8;
    }

    private readonly Dictionary<AmbientSound, Voice> _voices;
    private readonly Dictionary<AmbientSound, double> _volumes = new();
    private readonly HashSet<AmbientSound> _active = new();
    private readonly DebouncedSaver _saver;
    private readonly object _lock = new();
    private WaveOutPlayer? _player;
    private double _master;
    // Audio thread only.
    private volatile float _masterTarget;
    private float _masterSmooth;
    private int _silentFrames;

    /// Raised when sounds start or stop.
    public event Action? Changed;

    private AmbienceEngine()
    {
        var saved = Store.Load<Saved>("ambience");
        foreach (var sound in AmbientSoundInfo.All)
            _volumes[sound] = Math.Clamp(saved.Volumes.GetValueOrDefault(sound.Key(), 0.6), 0, 1);
        _master = Math.Clamp(saved.Master, 0, 1);
        _masterTarget = _masterSmooth = (float)_master;
        _voices = AmbientSoundInfo.All.ToDictionary(s => s, s => new Voice(s, SampleRate) { Volume = (float)_volumes[s] });
        _saver = new DebouncedSaver(TimeSpan.FromSeconds(0.5), Save);
    }

    public IReadOnlyCollection<AmbientSound> Active => _active;
    public bool IsPlaying => _active.Count > 0;
    public bool IsOn(AmbientSound sound) => _active.Contains(sound);

    public double MasterVolume
    {
        get => _master;
        set
        {
            _master = Math.Clamp(value, 0, 1);
            _masterTarget = (float)_master;
            _saver.Schedule();
        }
    }

    public double Volume(AmbientSound sound) => _volumes[sound];

    public void SetVolume(AmbientSound sound, double value)
    {
        _volumes[sound] = Math.Clamp(value, 0, 1);
        _voices[sound].Volume = (float)_volumes[sound];
        _saver.Schedule();
    }

    public void Toggle(AmbientSound sound)
    {
        if (_active.Contains(sound)) Stop(sound);
        else Play(sound);
    }

    public void Play(AmbientSound sound)
    {
        var voice = _voices[sound];
        voice.Volume = (float)_volumes[sound];
        voice.TargetOn = true;
        bool ok;
        lock (_lock)
        {
            // A player that died on an error is replaced.
            if (_player is { IsRunning: false }) _player = null;
            _player ??= WaveOutPlayer.Start(SampleRate, Render);
            ok = _player != null;
        }
        if (ok) _active.Add(sound);
        else voice.TargetOn = false; // no audio device: leave the tile off
        Changed?.Invoke();
    }

    public void Stop(AmbientSound sound)
    {
        _voices[sound].TargetOn = false;
        _active.Remove(sound);
        Changed?.Invoke();
    }

    public void StopAll()
    {
        foreach (var voice in _voices.Values) voice.TargetOn = false;
        _active.Clear();
        Changed?.Invoke();
    }

    /// Closes the audio device and saves volumes before Purrch quits. Does nothing if Ambience never ran.
    public static void Shutdown()
    {
        if (_shared is not { } engine) return;
        engine._saver.Flush();
        WaveOutPlayer? player;
        lock (engine._lock)
        {
            player = engine._player;
            engine._player = null;
        }
        player?.Dispose();
    }

    /// Audio thread: mixes one buffer. Returns false once everything has been silent for a while,
    /// which closes the device.
    private bool Render(float[] mix)
    {
        Array.Clear(mix);
        var audible = false;
        foreach (var voice in _voices.Values)
            if (voice.RenderInto(mix)) audible = true;

        var master = _masterTarget;
        for (var i = 0; i < mix.Length; i++)
        {
            _masterSmooth += (master - _masterSmooth) * 0.0008f;
            mix[i] *= _masterSmooth;
        }

        _silentFrames = audible ? 0 : _silentFrames + mix.Length;
        if (_silentFrames < IdleSeconds * SampleRate) return true;
        lock (_lock)
        {
            // A sound started meanwhile: keep going.
            if (_voices.Values.Any(v => !v.IsSilent)) return true;
            _silentFrames = 0;
            _player = null;
            return false;
        }
    }

    private void Save()
    {
        Store.Save("ambience", new Saved
        {
            Volumes = _volumes.ToDictionary(p => p.Key.Key(), p => p.Value),
            Master = _master,
        });
    }
}
