using System.Runtime.InteropServices;
using Purrch.Core;

namespace Purrch.Features.Media;

/// Streams mono 16-bit PCM to the default output device through the Win32 waveOut API, on its own
/// thread. The caller fills float buffers; returning false from the fill stops playback.
internal sealed class WaveOutPlayer : IDisposable
{
    private const int BufferCount = 4;
    /// 50 ms per buffer at 48 kHz: four of them ride out a busy UI thread without crackling.
    private const int FramesPerBuffer = 2400;
    private const uint CallbackEvent = 0x00050000;
    private const int WhdrDone = 0x00000001;
    private static readonly UIntPtr WaveMapper = new(uint.MaxValue);
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.dwFlags));

    private readonly IntPtr _device;
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _data = new IntPtr[BufferCount];
    private readonly AutoResetEvent _bufferDone;
    private readonly Func<float[], bool> _fill;
    private readonly float[] _mix = new float[FramesPerBuffer];
    private readonly short[] _pcm = new short[FramesPerBuffer];
    private readonly Thread _thread;
    private volatile bool _stop;

    private WaveOutPlayer(IntPtr device, AutoResetEvent bufferDone, Func<float[], bool> fill)
    {
        _device = device;
        _bufferDone = bufferDone;
        _fill = fill;
        for (var i = 0; i < BufferCount; i++)
        {
            _data[i] = Marshal.AllocHGlobal(FramesPerBuffer * sizeof(short));
            _headers[i] = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { lpData = _data[i], dwBufferLength = FramesPerBuffer * sizeof(short) }, _headers[i], false);
            waveOutPrepareHeader(_device, _headers[i], HeaderSize);
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "Purrch ambience", Priority = ThreadPriority.AboveNormal };
    }

    /// Opens the default output device, or returns null (and logs why) when there is none.
    public static WaveOutPlayer? Start(int sampleRate, Func<float[], bool> fill)
    {
        var format = new WaveFormat
        {
            wFormatTag = 1, // PCM
            nChannels = 1,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)sampleRate * sizeof(short),
            nBlockAlign = sizeof(short),
            wBitsPerSample = 16,
        };
        var bufferDone = new AutoResetEvent(false);
        int result;
        IntPtr device;
        try
        {
            result = waveOutOpen(out device, WaveMapper, ref format, bufferDone.SafeWaitHandle.DangerousGetHandle(),
                IntPtr.Zero, CallbackEvent);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            result = -1;
            device = IntPtr.Zero;
        }
        if (result != 0)
        {
            // 2 = no device, 6 = no driver: a PC without speakers, or audio turned off.
            Log.Write($"Ambience: no audio output (waveOutOpen {result})");
            bufferDone.Dispose();
            return null;
        }
        var player = new WaveOutPlayer(device, bufferDone, fill);
        player._thread.Start();
        return player;
    }

    /// False once playback has ended, whether asked to or after an error.
    public bool IsRunning => !_stop;

    /// Stops at once and waits briefly for the device to close.
    public void Dispose()
    {
        _stop = true;
        try { _bufferDone.Set(); }
        catch (ObjectDisposedException) { return; } // already closed itself
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(1));
    }

    private void Run()
    {
        try
        {
            // Queue every buffer, then refill each one as the device hands it back.
            for (var i = 0; i < BufferCount && !_stop; i++) Queue(i);
            while (!_stop)
            {
                _bufferDone.WaitOne(TimeSpan.FromMilliseconds(100));
                for (var i = 0; i < BufferCount && !_stop; i++)
                    if ((Marshal.ReadInt32(_headers[i], FlagsOffset) & WhdrDone) != 0) Queue(i);
            }
        }
        catch (Exception e)
        {
            Log.Write($"Ambience stopped: {e}");
        }
        finally
        {
            _stop = true;
            Close();
        }
    }

    private void Queue(int index)
    {
        if (!_fill(_mix))
        {
            _stop = true;
            return;
        }
        for (var i = 0; i < _mix.Length; i++) _pcm[i] = (short)(Math.Clamp(_mix[i], -1f, 1f) * short.MaxValue);
        Marshal.Copy(_pcm, 0, _data[index], _pcm.Length);
        // Clear "done" so the loop doesn't refill this buffer again before the device has played it.
        Marshal.WriteInt32(_headers[index], FlagsOffset, Marshal.ReadInt32(_headers[index], FlagsOffset) & ~WhdrDone);
        waveOutWrite(_device, _headers[index], HeaderSize);
    }

    private void Close()
    {
        // Reset hands every queued buffer back, so they can be unprepared and freed.
        waveOutReset(_device);
        for (var i = 0; i < BufferCount; i++)
        {
            waveOutUnprepareHeader(_device, _headers[i], HeaderSize);
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_data[i]);
        }
        waveOutClose(_device);
        _bufferDone.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, UIntPtr deviceId, ref WaveFormat format, IntPtr callback,
                                          IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}
