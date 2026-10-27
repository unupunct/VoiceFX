using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceFX.Core.Audio;

static class Mmcss
{
    public static readonly Guid FloatSubType = new("00000003-0000-0010-8000-00aa00389b71");
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr AvSetMmThreadCharacteristics(string task, ref uint index);
    public static void BoostCurrentThread() { try { uint i = 0; AvSetMmThreadCharacteristics("Pro Audio", ref i); } catch { } }
}

/// <summary>WASAPI shared-mode microphone capture, converted to mono float blocks.</summary>
public sealed class AudioCaptureEngine : IDisposable
{
    WasapiCapture? _cap;
    float[] _mono = new float[16384];
    bool _boosted, _isFloat;
    int _channels, _bits;
    public int SampleRate { get; private set; }
    public int Channels => _channels;
    public double StreamLatencyMs { get; private set; }
    /// <summary>Raised on the capture thread with mono samples; the callee processes in place.</summary>
    public event Action<float[], int>? Block;
    public event Action<Exception?>? Stopped;

    public void Start(MMDevice device, int bufferMs)
    {
        Stop();
        _cap = new WasapiCapture(device, true, Math.Max(10, bufferMs));
        var wf = _cap.WaveFormat;
        _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                   (wf is WaveFormatExtensible ext && ext.SubFormat == Mmcss.FloatSubType);
        _bits = wf.BitsPerSample; _channels = Math.Max(1, wf.Channels); SampleRate = wf.SampleRate;
        if (!_isFloat && _bits != 16 && _bits != 24 && _bits != 32)
            throw new NotSupportedException($"Unsupported microphone format: {wf}");
        _boosted = false;
        _cap.DataAvailable += OnData;
        _cap.RecordingStopped += (_, e) => Stopped?.Invoke(e.Exception);
        _cap.StartRecording();
    }

    void OnData(object? s, WaveInEventArgs e)
    {
        if (!_boosted) { Mmcss.BoostCurrentThread(); _boosted = true; }
        int bytesPer = _bits / 8, frame = bytesPer * _channels;
        int frames = e.BytesRecorded / frame;
        if (frames <= 0) return;
        if (frames > _mono.Length) frames = _mono.Length; // never allocate on the audio thread
        var b = e.Buffer; float inv = 1f / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0; int o = f * frame;
            for (int c = 0; c < _channels; c++, o += bytesPer)
            {
                sum += _isFloat ? BitConverter.ToSingle(b, o)
                     : _bits == 16 ? BitConverter.ToInt16(b, o) / 32768f
                     : _bits == 24 ? ((b[o] << 8 | b[o + 1] << 16 | b[o + 2] << 24) >> 8) / 8388608f
                     : BitConverter.ToInt32(b, o) / 2147483648f;
            }
            _mono[f] = sum * inv;
        }
        Block?.Invoke(_mono, frames);
    }

    public void Stop()
    {
        var c = _cap; _cap = null;
        if (c == null) return;
        try { c.DataAvailable -= OnData; c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
    }
    public void Dispose() => Stop();
}

/// <summary>
/// One WASAPI render stream fed from a lock-free SPSC ring. The reader resamples (linear) to the device rate
/// and trims clock drift by nudging the read ratio, so capture and render clocks never over/underrun.
/// </summary>
public sealed class RingOutput : IDisposable
{
    const int Cap = 1 << 16;
    readonly float[] _ring = new float[Cap];
    long _w, _r;
    WasapiOut? _out;
    int _srcRate, _dstRate, _dstCh, _target;
    double _frac;
    bool _buffering = true;
    long _underruns, _overruns;
    bool _boosted;
    public event Action<Exception?>? Stopped;
    public long Underruns => Interlocked.Read(ref _underruns);
    public long Overruns => Interlocked.Read(ref _overruns);
    public string DeviceName { get; private set; } = "";
    public double StreamLatencyMs { get; private set; }
    public double FillMs => _srcRate == 0 ? 0 : (Volatile.Read(ref _w) - Volatile.Read(ref _r)) * 1000.0 / _srcRate;

    public void Start(MMDevice device, int srcRate, int bufferMs)
    {
        Stop();
        _srcRate = srcRate; _w = _r = 0; _frac = 0; _buffering = true; _boosted = false;
        _target = Math.Max(64, (int)(srcRate * Math.Max(5, bufferMs * 0.6) / 1000.0));
        DeviceName = device.FriendlyName; StreamLatencyMs = bufferMs;
        var mix = device.AudioClient.MixFormat;
        _dstRate = mix.SampleRate; _dstCh = Math.Max(1, mix.Channels);
        IWaveProvider prov = new RingProvider(this, _dstRate, _dstCh);
        bool mixFloat = mix.Encoding == WaveFormatEncoding.IeeeFloat ||
                        (mix is WaveFormatExtensible x && x.SubFormat == Mmcss.FloatSubType);
        if (!mixFloat)
        {
            if (mix.BitsPerSample != 16) throw new NotSupportedException($"Unsupported output format: {mix}");
            prov = new SampleToWaveProvider16(new WaveToSampleProvider(prov));
        }
        _out = new WasapiOut(device, AudioClientShareMode.Shared, true, Math.Max(10, bufferMs));
        _out.PlaybackStopped += (_, e) => Stopped?.Invoke(e.Exception);
        _out.Init(prov);
        _out.Play();
    }

    public void Write(float[] data, int n)
    {
        long w = Volatile.Read(ref _w), r = Volatile.Read(ref _r);
        if (w - r + n > Cap) { Interlocked.Increment(ref _overruns); return; }
        for (int i = 0; i < n; i++) _ring[(w + i) & (Cap - 1)] = data[i];
        Volatile.Write(ref _w, w + n);
    }

    internal int Pull(Span<float> dst, int frames, int ch)
    {
        if (!_boosted) { Mmcss.BoostCurrentThread(); _boosted = true; }
        long w = Volatile.Read(ref _w), r = _r;
        long fill = w - r;
        if (fill > _target * 6) { r = w - _target; fill = _target; Interlocked.Increment(ref _overruns); } // far ahead: resync
        if (_buffering) { if (fill >= _target) _buffering = false; }
        double nudge = Math.Clamp((fill - _target) / (double)_target, -1.0, 1.0) * 0.004;
        double step = (double)_srcRate / _dstRate * (1.0 + nudge);
        int o = 0;
        for (int f = 0; f < frames; f++)
        {
            float y = 0f;
            if (!_buffering)
            {
                if (w - r >= 2)
                {
                    float s0 = _ring[r & (Cap - 1)], s1 = _ring[(r + 1) & (Cap - 1)];
                    y = s0 + (s1 - s0) * (float)_frac;
                    _frac += step;
                    while (_frac >= 1.0) { _frac -= 1.0; r++; }
                }
                else { _buffering = true; Interlocked.Increment(ref _underruns); }
            }
            for (int c = 0; c < ch; c++) dst[o++] = y;
        }
        Volatile.Write(ref _r, r);
        return frames;
    }

    public void Stop()
    {
        var o = _out; _out = null;
        if (o == null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
    }
    public void Dispose() => Stop();

    sealed class RingProvider : IWaveProvider
    {
        readonly RingOutput _o; readonly int _ch;
        public WaveFormat WaveFormat { get; }
        public RingProvider(RingOutput o, int rate, int ch) { _o = o; _ch = ch; WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, ch); }
        public int Read(byte[] buffer, int offset, int count)
        {
            var span = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count));
            int frames = span.Length / _ch;
            _o.Pull(span, frames, _ch);
            return frames * _ch * 4;
        }
    }
}

public interface IVirtualMicrophoneRouter : IDisposable
{
    bool IsConnected { get; }
    string? VirtualDeviceName { get; }
    bool MonitorActive { get; }
    long Underruns { get; }
    double LatencyMs { get; }
    void Write(float[] data, int n);
}

/// <summary>
/// Sends processed audio to the selected output device (normally the input side of a virtual audio cable,
/// e.g. "CABLE Input"), plus an optional separate monitor output (off by default to prevent feedback).
/// </summary>
public sealed class VirtualMicrophoneRouter : IVirtualMicrophoneRouter
{
    readonly RingOutput _main = new(), _monitor = new();
    volatile bool _monitorOn;
    public bool IsConnected { get; private set; }
    public string? VirtualDeviceName { get; private set; }
    public bool MonitorActive => _monitorOn;
    public long Underruns => _main.Underruns + (_monitorOn ? _monitor.Underruns : 0);
    public double LatencyMs => _main.FillMs + _main.StreamLatencyMs;
    public event Action<string, Exception?>? OutputFailed;

    public void Start(MMDevice device, int srcRate, int bufferMs)
    {
        _main.Stopped -= OnMainStopped; _main.Stopped += OnMainStopped;
        _main.Start(device, srcRate, bufferMs);
        VirtualDeviceName = device.FriendlyName; IsConnected = true;
    }

    void OnMainStopped(Exception? e) { IsConnected = false; if (e != null) OutputFailed?.Invoke("Output device stopped", e); }

    public void StartMonitor(MMDevice device, int srcRate, int bufferMs)
    {
        _monitor.Stopped -= OnMonitorStopped; _monitor.Stopped += OnMonitorStopped;
        _monitor.Start(device, srcRate, bufferMs);
        _monitorOn = true;
    }
    void OnMonitorStopped(Exception? e) { _monitorOn = false; if (e != null) OutputFailed?.Invoke("Monitor device stopped", e); }
    public void StopMonitor() { _monitorOn = false; _monitor.Stop(); }

    public void Write(float[] data, int n)
    {
        _main.Write(data, n);
        if (_monitorOn) _monitor.Write(data, n);
    }

    public void Stop() { IsConnected = false; _monitorOn = false; _main.Stop(); _monitor.Stop(); }
    public void Dispose() => Stop();
}
