import AVFoundation
import SwiftUI

// MARK: - DSP building blocks (render thread only)

private struct Rng {
    var state: UInt32
    init(seed: UInt32) { state = seed == 0 ? 0x9E37_79B9 : seed }
    /// Uniform in [-1, 1].
    mutating func white() -> Float {
        state ^= state << 13
        state ^= state >> 17
        state ^= state << 5
        return Float(Int32(bitPattern: state)) / Float(Int32.max)
    }
    /// Uniform in [0, 1).
    mutating func unit() -> Float { white() * 0.5 + 0.5 }
}

/// Paul Kellet's economical pink-noise filter.
private struct Pink {
    var b0: Float = 0, b1: Float = 0, b2: Float = 0, b3: Float = 0, b4: Float = 0, b5: Float = 0, b6: Float = 0
    mutating func next(_ w: Float) -> Float {
        b0 = 0.99886 * b0 + w * 0.0555179
        b1 = 0.99332 * b1 + w * 0.0750759
        b2 = 0.96900 * b2 + w * 0.1538520
        b3 = 0.86650 * b3 + w * 0.3104856
        b4 = 0.55000 * b4 + w * 0.5329522
        b5 = -0.7616 * b5 - w * 0.0168980
        let out = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362
        b6 = w * 0.115926
        return out * 0.11
    }
}

/// Leaky-integrated (brown) noise.
private struct Brown {
    var last: Float = 0
    mutating func next(_ w: Float) -> Float {
        last = (last + 0.02 * w) / 1.02
        return last * 3.5
    }
}

/// RBJ biquad.
private struct Biquad {
    enum Kind { case lowpass, highpass, bandpass }
    var b0: Float = 1, b1: Float = 0, b2: Float = 0, a1: Float = 0, a2: Float = 0
    var x1: Float = 0, x2: Float = 0, y1: Float = 0, y2: Float = 0

    init(_ kind: Kind, freq: Float, q: Float, sr: Float) { set(kind, freq: freq, q: q, sr: sr) }

    mutating func set(_ kind: Kind, freq: Float, q: Float, sr: Float) {
        let f = min(max(freq, 10), sr * 0.45)
        let w0 = 2 * Float.pi * f / sr
        let cosw = cos(w0), alpha = sin(w0) / (2 * max(q, 0.05))
        let a0 = 1 + alpha
        switch kind {
        case .lowpass:
            b0 = (1 - cosw) / 2; b1 = 1 - cosw; b2 = (1 - cosw) / 2
        case .highpass:
            b0 = (1 + cosw) / 2; b1 = -(1 + cosw); b2 = (1 + cosw) / 2
        case .bandpass:
            b0 = alpha; b1 = 0; b2 = -alpha
        }
        a1 = -2 * cosw; a2 = 1 - alpha
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0
    }

    mutating func process(_ x: Float) -> Float {
        let y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2 = x1; x1 = x; y2 = y1; y1 = y
        return y
    }
}

/// One-pole lowpass, cheap smoothing.
private struct OnePole {
    var coeff: Float
    var z: Float = 0
    init(freq: Float, sr: Float) { coeff = exp(-2 * Float.pi * freq / sr) }
    mutating func process(_ x: Float) -> Float { z = x + coeff * (z - x); return z }
}

/// A decaying sinusoidal "plink" (rain drops, bubbles, crackles).
private struct Blip {
    var env: Float = 0
    var decay: Float = 0.999
    var phase: Float = 0
    var inc: Float = 0
    var chirp: Float = 1
    mutating func trigger(amp: Float, freq: Float, decaySeconds: Float, chirp: Float = 1, sr: Float) {
        env = amp
        decay = exp(-1 / (decaySeconds * sr))
        inc = 2 * Float.pi * freq / sr
        self.chirp = chirp
        phase = 0
    }
    mutating func next() -> Float {
        guard env > 0.0001 else { return 0 }
        let v = sin(phase) * env
        phase += inc
        if phase > 2 * Float.pi { phase -= 2 * Float.pi }
        inc *= chirp
        env *= decay
        return v
    }
}

// MARK: - Sounds

enum AmbientSound: String, CaseIterable, Identifiable, Codable {
    case rain, storm, wind, ocean, fire, stream, lofi, white, pink, brown

    var id: String { rawValue }

    var title: String {
        switch self {
        case .rain: "Rain"
        case .storm: "Storm"
        case .wind: "Wind"
        case .ocean: "Ocean"
        case .fire: "Fireplace"
        case .stream: "Stream"
        case .lofi: "Lo-fi Hum"
        case .white: "White Noise"
        case .pink: "Pink Noise"
        case .brown: "Brown Noise"
        }
    }

    var icon: String {
        switch self {
        case .rain: "cloud.drizzle.fill"
        case .storm: "cloud.bolt.rain.fill"
        case .wind: "wind"
        case .ocean: "water.waves"
        case .fire: "flame.fill"
        case .stream: "drop.fill"
        case .lofi: "music.quarternote.3"
        case .white: "waveform"
        case .pink: "waveform.path"
        case .brown: "waveform.path.ecg"
        }
    }
}

/// Per-sound synthesizer. `targetOn` and `volume` are written from the main thread and read
/// on the render thread; plain Float/Bool stores are fine for this purpose.
private final class Voice {
    let sound: AmbientSound
    let sr: Float
    var targetOn = false
    var volume: Float = 0.6

    private var fade: Float = 0
    private var smoothVol: Float = 0.6
    private let fadeStep: Float
    private var rng: Rng
    private var pink = Pink()
    private var brown = Brown()
    private var brown2 = Brown()
    private var f1: Biquad
    private var f2: Biquad
    private var f3: Biquad
    private var lp: OnePole
    private var blips = [Blip](repeating: Blip(), count: 8)
    private var blipIndex = 0
    private var t: Double = 0
    private var counter = 0
    private var rumbleEnv: Float = 0
    private var rumbleTarget: Float = 0
    private var crackleEnv: Float = 0
    private var crackleDecay: Float = 0.99
    // Lo-fi
    private var phases = [Float](repeating: 0, count: 8)
    private var freqs = [Float](repeating: 110, count: 8)
    private var targetFreqs = [Float](repeating: 110, count: 8)
    private var chordIndex = 0
    private var chordTimer: Double = 0

    var isSilent: Bool { !targetOn && fade <= 0 }

    init(_ sound: AmbientSound, sampleRate: Double) {
        self.sound = sound
        sr = Float(sampleRate)
        fadeStep = 1 / (Float(sampleRate) * 1.0)
        rng = Rng(seed: UInt32(truncatingIfNeeded: sound.rawValue.hashValue) | 1)
        lp = OnePole(freq: 400, sr: Float(sampleRate))
        switch sound {
        case .rain:
            f1 = Biquad(.highpass, freq: 500, q: 0.7, sr: sr)
            f2 = Biquad(.lowpass, freq: 7000, q: 0.7, sr: sr)
            f3 = Biquad(.lowpass, freq: 120, q: 0.7, sr: sr)
        case .storm:
            f1 = Biquad(.highpass, freq: 300, q: 0.7, sr: sr)
            f2 = Biquad(.lowpass, freq: 4500, q: 0.7, sr: sr)
            f3 = Biquad(.lowpass, freq: 90, q: 0.8, sr: sr)
        case .wind:
            f1 = Biquad(.bandpass, freq: 500, q: 1.2, sr: sr)
            f2 = Biquad(.lowpass, freq: 2500, q: 0.7, sr: sr)
            f3 = Biquad(.bandpass, freq: 1200, q: 4, sr: sr)
        case .ocean:
            f1 = Biquad(.lowpass, freq: 800, q: 0.7, sr: sr)
            f2 = Biquad(.highpass, freq: 1500, q: 0.7, sr: sr)
            f3 = Biquad(.lowpass, freq: 6000, q: 0.7, sr: sr)
        case .fire:
            f1 = Biquad(.lowpass, freq: 180, q: 0.7, sr: sr)
            f2 = Biquad(.highpass, freq: 1200, q: 0.7, sr: sr)
            f3 = Biquad(.bandpass, freq: 600, q: 0.8, sr: sr)
        case .stream:
            f1 = Biquad(.bandpass, freq: 1800, q: 0.6, sr: sr)
            f2 = Biquad(.highpass, freq: 300, q: 0.7, sr: sr)
            f3 = Biquad(.lowpass, freq: 6000, q: 0.7, sr: sr)
        case .lofi:
            f1 = Biquad(.lowpass, freq: 1400, q: 0.7, sr: sr)
            f2 = Biquad(.highpass, freq: 2500, q: 0.7, sr: sr)
            f3 = Biquad(.bandpass, freq: 3000, q: 1, sr: sr)
        case .white, .pink, .brown:
            f1 = Biquad(.lowpass, freq: 16000, q: 0.7, sr: sr)
            f2 = Biquad(.highpass, freq: 25, q: 0.7, sr: sr)
            f3 = Biquad(.lowpass, freq: 16000, q: 0.7, sr: sr)
        }
        if sound == .lofi { setChord(0, immediate: true) }
    }

    func render(into buffers: UnsafeMutableAudioBufferListPointer, frames: Int) -> Bool {
        let target: Float = targetOn ? 1 : 0
        if fade <= 0 && target == 0 {
            for b in buffers { memset(b.mData, 0, Int(b.mDataByteSize)) }
            return true
        }
        let dt = 1 / Double(sr)
        guard let first = buffers.first?.mData?.assumingMemoryBound(to: Float.self) else { return true }
        for i in 0..<frames {
            if fade < target { fade = min(target, fade + fadeStep) } else if fade > target { fade = max(target, fade - fadeStep) }
            smoothVol += (volume - smoothVol) * 0.0008
            let s = sample()
            // Equal-power-ish fade curve.
            first[i] = s * fade * fade * smoothVol
            t += dt
        }
        for b in buffers.dropFirst() {
            if let d = b.mData { memcpy(d, first, frames * MemoryLayout<Float>.size) }
        }
        return false
    }

    private func nextBlip() -> Int {
        blipIndex = (blipIndex + 1) % blips.count
        return blipIndex
    }

    private func blipSum() -> Float {
        var s: Float = 0
        for i in 0..<blips.count { s += blips[i].next() }
        return s
    }

    private func setChord(_ index: Int, immediate: Bool = false) {
        // Soft jazzy progression: Cmaj7 - Am7 - Fmaj7 - G6 (low register).
        let chords: [[Float]] = [
            [130.81, 164.81, 196.00, 246.94],
            [110.00, 130.81, 164.81, 196.00],
            [87.31, 130.81, 164.81, 220.00],
            [98.00, 123.47, 146.83, 164.81],
        ]
        let chord = chords[index % chords.count]
        for n in 0..<4 {
            targetFreqs[n * 2] = chord[n] * 0.997
            targetFreqs[n * 2 + 1] = chord[n] * 1.003
            if immediate { freqs[n * 2] = targetFreqs[n * 2]; freqs[n * 2 + 1] = targetFreqs[n * 2 + 1] }
        }
    }

    private func sample() -> Float {
        let w = rng.white()
        counter &+= 1
        switch sound {
        case .white:
            return w * 0.25
        case .pink:
            return pink.next(w) * 0.9
        case .brown:
            return f2.process(brown.next(w)) * 0.8

        case .rain:
            let hiss = f2.process(f1.process(pink.next(w))) * 0.55
            let body = f3.process(brown.next(rng.white())) * 0.25
            if rng.unit() < 45 / sr {
                let i = nextBlip()
                blips[i].trigger(amp: 0.04 + rng.unit() * 0.12, freq: 1800 + rng.unit() * 3500,
                                 decaySeconds: 0.004 + rng.unit() * 0.012, chirp: 0.9998, sr: sr)
            }
            return hiss + body + blipSum()

        case .storm:
            let swell = Float(0.8 + 0.2 * sin(t * 2 * .pi * 0.07))
            let hiss = f2.process(f1.process(pink.next(w))) * 0.8 * swell
            if rng.unit() < 90 / sr {
                let i = nextBlip()
                blips[i].trigger(amp: 0.05 + rng.unit() * 0.12, freq: 1200 + rng.unit() * 3000,
                                 decaySeconds: 0.003 + rng.unit() * 0.01, sr: sr)
            }
            // Thunder: rare slow envelope over very low brown noise.
            if rumbleTarget == 0, rng.unit() < 1 / (sr * 14) { rumbleTarget = 0.6 + rng.unit() * 0.6 }
            if rumbleTarget > 0 {
                rumbleEnv += (rumbleTarget - rumbleEnv) * (1 / (sr * 0.6))
                if rumbleEnv >= rumbleTarget * 0.97 { rumbleTarget = 0 }
            } else {
                rumbleEnv *= exp(-1 / (sr * 2.5))
            }
            let rumble = f3.process(brown2.next(rng.white())) * 2.2 * rumbleEnv
            return hiss + blipSum() + rumble

        case .wind:
            if counter % 64 == 0 {
                let m = 0.5 + 0.3 * sin(t * 2 * .pi * 0.05) + 0.2 * sin(t * 2 * .pi * 0.13 + 1.7)
                f1.set(.bandpass, freq: Float(250 + 650 * m), q: 1.1, sr: sr)
                f3.set(.bandpass, freq: Float(900 + 900 * m), q: 6, sr: sr)
            }
            let gust = Float(0.45 + 0.35 * sin(t * 2 * .pi * 0.08) + 0.2 * sin(t * 2 * .pi * 0.21 + 0.5))
            let base = f2.process(f1.process(w)) * 0.9
            let whistle = f3.process(w) * 0.12 * gust * gust
            return (base + whistle) * max(0.1, gust)

        case .ocean:
            // ~0.1 Hz swell, sharpened so waves crash then recede.
            let phase = (sin(t * 2 * .pi * 0.1) + 1) / 2
            let swell = Float(pow(phase, 2.2))
            if counter % 64 == 0 { f1.set(.lowpass, freq: 300 + 1500 * swell, q: 0.7, sr: sr) }
            let rumble = f1.process(brown.next(w)) * (0.25 + 0.75 * swell)
            let foam = f3.process(f2.process(pink.next(rng.white()))) * 0.35 * swell * swell
            return (rumble + foam) * 0.9

        case .fire:
            let roar = f1.process(brown.next(w)) * 0.55
            let hiss = f2.process(w) * 0.015 * (0.6 + 0.4 * rng.unit())
            // Crackle: sparse noise pops with fast decay, occasionally in bursts.
            if rng.unit() < 7 / sr || (crackleEnv > 0.05 && rng.unit() < 120 / sr) {
                crackleEnv = 0.25 + rng.unit() * 0.75
                crackleDecay = exp(-1 / (sr * (0.0006 + rng.unit() * 0.003)))
            }
            let pop = f3.process(rng.white()) * crackleEnv * 1.6
            crackleEnv *= crackleDecay
            return roar + hiss + pop

        case .stream:
            if counter % 64 == 0 {
                let m = Float(0.5 + 0.25 * sin(t * 2 * .pi * 3.1) * sin(t * 2 * .pi * 0.37) + 0.25 * sin(t * 2 * .pi * 5.3 + 1))
                f1.set(.bandpass, freq: 1200 + 1600 * m, q: 0.7, sr: sr)
            }
            let water = f3.process(f2.process(f1.process(pink.next(w)))) * 0.9
            if rng.unit() < 14 / sr {
                let i = nextBlip()
                blips[i].trigger(amp: 0.03 + rng.unit() * 0.06, freq: 500 + rng.unit() * 900,
                                 decaySeconds: 0.015 + rng.unit() * 0.03, chirp: 1.00004, sr: sr)
            }
            return water + blipSum()

        case .lofi:
            chordTimer += 1 / Double(sr)
            if chordTimer > 4 { chordTimer = 0; chordIndex += 1; setChord(chordIndex) }
            let wow = 1 + 0.0025 * Float(sin(t * 2 * .pi * 0.5))
            var tone: Float = 0
            for n in 0..<8 {
                freqs[n] += (targetFreqs[n] - freqs[n]) * 0.00008
                phases[n] += 2 * .pi * freqs[n] * wow / sr
                if phases[n] > 2 * .pi { phases[n] -= 2 * .pi }
                // Sine with a touch of 2nd harmonic for warmth.
                tone += sin(phases[n]) + 0.15 * sin(phases[n] * 2)
            }
            let breathe = Float(0.75 + 0.25 * sin(t * 2 * .pi * 0.25))
            tone = f1.process(tone * 0.05 * breathe)
            let hiss = f2.process(pink.next(w)) * 0.03
            // Vinyl crackle.
            if rng.unit() < 5 / sr { crackleEnv = 0.1 + rng.unit() * 0.25 }
            let click = f3.process(rng.white()) * crackleEnv
            crackleEnv *= 0.993
            return tone + hiss + click
        }
    }
}

// MARK: - Engine

@MainActor
final class AmbienceEngine: ObservableObject {
    static let shared = AmbienceEngine()

    @Published private(set) var active: Set<AmbientSound> = []
    @Published var volumes: [AmbientSound: Double] = [:] { didSet { pushVolumes(); save() } }
    @Published var masterVolume: Double = 0.8 {
        didSet { engine.mainMixerNode.outputVolume = Float(masterVolume); save() }
    }

    private let engine = AVAudioEngine()
    private var voices: [AmbientSound: Voice] = [:]
    private var built = false
    private var idleTimer: Timer?

    private struct Saved: Codable {
        var volumes: [String: Double] = [:]
        var master: Double = 0.8
    }

    private init() {
        let saved = Store.load("ambience", default: Saved())
        var v: [AmbientSound: Double] = [:]
        for s in AmbientSound.allCases { v[s] = saved.volumes[s.rawValue] ?? 0.6 }
        volumes = v
        masterVolume = saved.master
    }

    var isPlaying: Bool { !active.isEmpty }

    func isOn(_ sound: AmbientSound) -> Bool { active.contains(sound) }

    func toggle(_ sound: AmbientSound) {
        if active.contains(sound) { stop(sound) } else { play(sound) }
    }

    func play(_ sound: AmbientSound) {
        buildIfNeeded()
        guard let voice = voices[sound] else { return }
        voice.volume = Float(volumes[sound] ?? 0.6)
        voice.targetOn = true
        active.insert(sound)
        idleTimer?.invalidate()
        idleTimer = nil
        if !engine.isRunning {
            do { try engine.start() } catch {
                NSLog("Purrch ambience: \(error.localizedDescription)")
                voice.targetOn = false
                active.remove(sound)
            }
        }
    }

    func stop(_ sound: AmbientSound) {
        voices[sound]?.targetOn = false
        active.remove(sound)
        scheduleIdleStop()
    }

    func stopAll() {
        for v in voices.values { v.targetOn = false }
        active.removeAll()
        scheduleIdleStop()
    }

    private func buildIfNeeded() {
        guard !built else { return }
        built = true
        let rate = engine.outputNode.outputFormat(forBus: 0).sampleRate
        let sampleRate = rate > 0 ? rate : 48_000
        guard let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1) else { return }
        for sound in AmbientSound.allCases {
            let voice = Voice(sound, sampleRate: sampleRate)
            voice.volume = Float(volumes[sound] ?? 0.6)
            let node = AVAudioSourceNode(format: format) { isSilence, _, frameCount, bufferList in
                let buffers = UnsafeMutableAudioBufferListPointer(bufferList)
                let silent = voice.render(into: buffers, frames: Int(frameCount))
                isSilence.pointee = ObjCBool(silent)
                return noErr
            }
            engine.attach(node)
            engine.connect(node, to: engine.mainMixerNode, format: format)
            voices[sound] = voice
        }
        engine.mainMixerNode.outputVolume = Float(masterVolume)
        engine.prepare()
    }

    /// Pauses the engine once every voice has faded out, so we use no CPU when silent.
    private func scheduleIdleStop() {
        guard active.isEmpty else { return }
        idleTimer?.invalidate()
        let t = Timer(timeInterval: 1.6, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.active.isEmpty, self.voices.values.allSatisfy(\.isSilent) else { return }
                self.engine.pause()
            }
        }
        RunLoop.main.add(t, forMode: .common)
        idleTimer = t
    }

    private func pushVolumes() {
        for (sound, voice) in voices { voice.volume = Float(volumes[sound] ?? 0.6) }
    }

    private func save() {
        var v: [String: Double] = [:]
        for (k, val) in volumes { v[k.rawValue] = val }
        Store.save("ambience", Saved(volumes: v, master: masterVolume))
    }
}

// MARK: - View

struct AmbienceModule: View {
    @Environment(\.theme) private var theme
    @ObservedObject private var engine = AmbienceEngine.shared

    private let columns = Array(repeating: GridItem(.flexible(), spacing: 8), count: 5)

    var body: some View {
        VStack(spacing: 10) {
            HStack(spacing: 12) {
                Image(systemName: "speaker.wave.2.fill")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(theme.accent)
                Text(engine.isPlaying ? "\(engine.active.count) playing" : "Tap sounds to layer them")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(engine.isPlaying ? theme.text : theme.secondaryText)
                if engine.isPlaying {
                    MusicBars(color: theme.accent).frame(width: 16, height: 12)
                }
                Spacer()
                Image(systemName: "speaker.fill").font(.system(size: 10)).foregroundStyle(theme.secondaryText)
                Slider(value: $engine.masterVolume, in: 0...1)
                    .controlSize(.small)
                    .tint(theme.accent)
                    .frame(width: 150)
                Image(systemName: "speaker.wave.3.fill").font(.system(size: 10)).foregroundStyle(theme.secondaryText)
                PillButton(title: "Stop all", icon: "stop.fill", prominent: engine.isPlaying) {
                    engine.stopAll()
                }
                .opacity(engine.isPlaying ? 1 : 0.5)
                .disabled(!engine.isPlaying)
            }
            .padding(.horizontal, 4)

            LazyVGrid(columns: columns, spacing: 8) {
                ForEach(AmbientSound.allCases) { sound in
                    SoundTile(sound: sound, engine: engine)
                }
            }
            Spacer(minLength: 0)
        }
    }
}

private struct SoundTile: View {
    @Environment(\.theme) private var theme
    let sound: AmbientSound
    @ObservedObject var engine: AmbienceEngine
    @State private var hovering = false

    var body: some View {
        let on = engine.isOn(sound)
        VStack(spacing: 6) {
            Button {
                withAnimation(.easeOut(duration: 0.2)) { engine.toggle(sound) }
            } label: {
                VStack(spacing: 6) {
                    ZStack {
                        Circle()
                            .fill(on ? theme.accent : theme.text.opacity(hovering ? 0.14 : 0.08))
                            .frame(width: 38, height: 38)
                            .themeGlow(on ? theme : Theme.make(.minimal), radius: 6)
                        Image(systemName: sound.icon)
                            .font(.system(size: 16, weight: .semibold))
                            .foregroundStyle(on ? theme.background : theme.text)
                            .symbolEffect(.pulse, options: .repeating, isActive: on)
                    }
                    Text(sound.title)
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(on ? theme.text : theme.secondaryText)
                        .lineLimit(1)
                }
                .frame(maxWidth: .infinity)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)

            Slider(value: Binding(
                get: { engine.volumes[sound] ?? 0.6 },
                set: { engine.volumes[sound] = $0 }
            ), in: 0...1)
            .controlSize(.mini)
            .tint(theme.accent)
            .opacity(on ? 1 : 0.45)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 10)
        .frame(height: 112)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(on ? theme.accent.opacity(0.14) : theme.surface)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(on ? theme.accent.opacity(0.8) : theme.border, lineWidth: on ? 1.2 : 0.8)
        )
        .onHover { hovering = $0 }
    }
}
