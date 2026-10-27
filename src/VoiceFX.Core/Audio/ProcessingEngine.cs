using VoiceFX.Core.Dsp;
using VoiceFX.Core.Effects;

namespace VoiceFX.Core.Audio;

public interface IAudioMeterService
{
    /// <summary>Reads and resets the peaks accumulated since the previous call.</summary>
    (float input, float output, bool clipped) Read();
}

/// <summary>Lock-free peak meters: written by the audio thread, polled by the UI thread.</summary>
public sealed class AudioMeterService : IAudioMeterService
{
    volatile float _in, _out; volatile bool _clip;
    public void Report(float input, float output, bool clip)
    {
        if (input > _in) _in = input;
        if (output > _out) _out = output;
        if (clip) _clip = true;
    }
    public (float, float, bool) Read()
    {
        var r = (_in, _out, _clip); _in = 0; _out = 0; _clip = false; return r;
    }
}

/// <summary>
/// Real-time DSP: input gain → (gate / noise reduction) → preset chain (cross-faded on switch) →
/// bypass blend → master gain → mute → limiter. Allocation-free and lock-free in <see cref="Process"/>.
/// </summary>
public sealed class AudioProcessingEngine
{
    const int MaxBlock = 2048;
    readonly IVoicePresetManager _presets;
    readonly AudioMeterService _meters;
    readonly NoiseGate _gate = new(), _nr = new();
    readonly Limiter _limiter = new();
    readonly float[] _raw = new float[MaxBlock], _g = new float[MaxBlock], _a = new float[MaxBlock], _b = new float[MaxBlock];
    Smoother _inGain, _masterGain, _muteGain, _procBlend, _intensity;
    int _sr = 48000, _active, _previous = -1, _fadeLeft, _fadeLen;
    int _requested;

    // Controls (UI thread writes, audio thread reads).
    public volatile int PresetIndex;
    public float Intensity = 1f, InputGainDb, MasterGainDb, GateThresholdDb = -50f;
    public volatile bool Muted, Bypass, GateEnabled, NoiseReduction;

    public AudioProcessingEngine(IVoicePresetManager presets, AudioMeterService meters)
    {
        _presets = presets; _meters = meters;
        Prepare(48000);
    }

    public int LatencySamples => _presets.Presets[_active].LatencySamples + _limiter.LatencySamples;
    public int SampleRate => _sr;
    public int ActivePreset => _active;

    public void Prepare(int sampleRate)
    {
        _sr = sampleRate;
        _presets.Initialize(sampleRate);
        _gate.Prepare(sampleRate); _nr.Prepare(sampleRate); _limiter.Prepare(sampleRate);
        _inGain.Init(DspMath.DbToLin(InputGainDb), 15, sampleRate);
        _masterGain.Init(DspMath.DbToLin(MasterGainDb), 15, sampleRate);
        _muteGain.Init(Muted ? 0f : 1f, 12, sampleRate);
        _procBlend.Init(Bypass ? 0f : 1f, 15, sampleRate);
        _intensity.Init(Intensity, 25, sampleRate);
        _active = _requested = Math.Clamp(PresetIndex, 0, _presets.Presets.Count - 1);
        _previous = -1; _fadeLeft = 0; _fadeLen = Math.Max(1, (int)(sampleRate * 0.03));
    }

    public void Process(Span<float> data)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            int n = Math.Min(MaxBlock, data.Length - pos);
            ProcessBlock(data.Slice(pos, n));
            pos += n;
        }
    }

    void ProcessBlock(Span<float> d)
    {
        int n = d.Length;
        float inPeak = 0f;
        _inGain.Set(DspMath.DbToLin(InputGainDb));
        _masterGain.Set(DspMath.DbToLin(MasterGainDb));
        _muteGain.Set(Muted ? 0f : 1f);
        _procBlend.Set(Bypass ? 0f : 1f);
        _intensity.Set(Math.Clamp(Intensity, 0f, 1f));

        bool clip = false;
        for (int i = 0; i < n; i++)
        {
            float x = d[i];
            if (!float.IsFinite(x)) x = 0f;
            float ax = MathF.Abs(x); if (ax > inPeak) inPeak = ax;
            if (ax >= 0.999f) clip = true;
            x *= _inGain.Next();
            _raw[i] = x;
        }

        bool gate = GateEnabled, nr = NoiseReduction; float thr = GateThresholdDb;
        for (int i = 0; i < n; i++)
        {
            float x = _raw[i];
            if (gate) x = _gate.Process(x, thr, -80f);
            if (nr) x = _nr.Process(x, thr - 6f, -14f);
            _g[i] = x; _a[i] = x;
        }

        // Preset switching: cross-fade old and new chain, no engine restart.
        int want = Math.Clamp(PresetIndex, 0, _presets.Presets.Count - 1);
        if (want != _active)
        {
            _previous = _active; _active = want; _fadeLeft = _fadeLen;
            _presets.Presets[_active].Reset();
        }
        float inten = _intensity.Next();
        var cur = _presets.Presets[_active];
        cur.Process(_a.AsSpan(0, n), inten);

        if (_fadeLeft > 0 && _previous >= 0)
        {
            Array.Copy(_g, _b, n);
            _presets.Presets[_previous].Process(_b.AsSpan(0, n), inten);
            for (int i = 0; i < n; i++)
            {
                float t = _fadeLeft > 0 ? 1f - (float)_fadeLeft / _fadeLen : 1f;
                _a[i] = _b[i] + (_a[i] - _b[i]) * t;
                if (_fadeLeft > 0) _fadeLeft--;
            }
            if (_fadeLeft == 0) _previous = -1;
        }

        float preLimit = 0f, outPeak = 0f;
        for (int i = 0; i < n; i++)
        {
            float bp = _procBlend.Next();
            float y = _raw[i] + (_a[i] - _raw[i]) * bp;
            y *= _masterGain.Next();
            float ay = MathF.Abs(y); if (ay > preLimit) preLimit = ay;
            y = _limiter.Process(y) * _muteGain.Next();
            if (!float.IsFinite(y)) y = 0f;
            d[i] = y;
            ay = MathF.Abs(y); if (ay > outPeak) outPeak = ay;
        }
        _meters.Report(inPeak, outPeak, clip || preLimit > 1.0f);
    }
}
