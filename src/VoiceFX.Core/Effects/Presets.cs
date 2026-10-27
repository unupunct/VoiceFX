using VoiceFX.Core.Dsp;

namespace VoiceFX.Core.Effects;

/// <summary>1 — Natural / Clean: rumble filter, light expander, gentle compression.</summary>
public sealed class NaturalEffect : VoiceEffect
{
    readonly Biquad _hp = new(), _hp2 = new();
    readonly Compressor _comp = new();
    readonly NoiseGate _exp = new();
    float _hz = 80, _nrFloor = -10;

    public override string Id => "natural";
    public override string Name => "NATURAL";
    public override string Icon => "🎙";
    public override string Description => "Transparent voice: high-pass, light noise reduction, gentle compression.";
    public override ParamDef[] Params { get; } =
    {
        new("hp", "Rumble filter", 40, 200, 80, "Hz"),
        new("comp", "Compression", 0, 1, 0.5f),
        new("nr", "Noise reduction", 0, 1, 0.3f),
    };

    protected override void OnPrepare() { _comp.Prepare(SampleRate, 8, 140); _exp.Prepare(SampleRate); }
    public override void Reset() { _hp.Reset(); _hp2.Reset(); _comp.Reset(); _exp.Prepare(SampleRate); }

    protected override void ProcessCore(Span<float> buf, float i, bool dirty)
    {
        if (dirty)
        {
            _hz = Values[0];
            _hp.HighPass(SampleRate, _hz); _hp2.HighPass(SampleRate, _hz);
            float c = Values[1];
            _comp.Set(-20f, 1f + 3f * c, 5f * c);
            _nrFloor = -(4f + 20f * Values[2]);
        }
        for (int k = 0; k < buf.Length; k++)
        {
            float x = buf[k];
            float y = _hp2.Process(_hp.Process(x));
            if (Values[2] > 0.01f) y = _exp.Process(y, -54f, _nrFloor);
            y = _comp.Process(y);
            buf[k] = x + (y - x) * i;
        }
    }
}

/// <summary>2 — Deep Radio Voice: pitch down, warmth, broadcast compression.</summary>
public sealed class DeepRadioEffect : VoiceEffect
{
    readonly PitchShifter _ps = new();
    readonly Biquad _hp = new(), _ls = new(), _pk = new(), _hs = new();
    readonly Compressor _comp = new();
    readonly DelayLine _dry = new();
    Smoother _ratio, _wet;

    public override string Id => "deep";
    public override string Name => "DEEP RADIO";
    public override string Icon => "📻";
    public override string Description => "Lower pitch (speed preserved), warm low-mids, broadcast compression.";
    public override ParamDef[] Params { get; } =
    {
        new("pitch", "Pitch depth", -8, 0, -3.5f, "st"),
        new("warmth", "Warmth", 0, 9, 4, "dB"),
        new("comp", "Broadcast compression", 0, 1, 0.6f),
    };
    public override int LatencySamples => _ps.LatencySamples;

    protected override void OnPrepare()
    {
        _ps.Prepare(SampleRate); _comp.Prepare(SampleRate, 5, 120);
        _hp.HighPass(SampleRate, 70);
        _dry.Prepare(_ps.LatencySamples + 8, _ps.LatencySamples);
        _ratio.Init(1f, 20, SampleRate); _wet.Init(0f, 20, SampleRate);
    }
    public override void Reset() { _ps.Reset(); _hp.Reset(); _ls.Reset(); _pk.Reset(); _hs.Reset(); _comp.Reset(); _dry.Reset(); }

    protected override void ProcessCore(Span<float> buf, float i, bool dirty)
    {
        if (dirty)
        {
            float w = Values[1] * i;
            _ls.LowShelf(SampleRate, 140, w); _pk.Peak(SampleRate, 320, 0.9, w * 0.5f); _hs.HighShelf(SampleRate, 7000, -2.5f * i);
            _comp.Set(-22f, 1f + 3.5f * Values[2], 6f * Values[2]);
            _ratio.Set(MathF.Pow(2f, Values[0] * i / 12f));
            _wet.Set(MathF.Min(1f, i * 4f));
        }
        for (int k = 0; k < buf.Length; k++)
        {
            float x = buf[k];
            float d = _dry.Process(x);
            float y = _ps.Process(x, _ratio.Next());
            y = _hs.Process(_pk.Process(_ls.Process(_hp.Process(y))));
            y = _comp.Process(y);
            float w = _wet.Next();
            buf[k] = d + (y - d) * w;
        }
    }
}

/// <summary>3 — Bright Presenter: presence + air, de-essing, controlled compression.</summary>
public sealed class BrightEffect : VoiceEffect
{
    readonly Biquad _hp = new(), _pres = new(), _air = new(), _thin = new();
    readonly Compressor _comp = new();
    readonly DeEsser _de = new();

    public override string Id => "bright";
    public override string Name => "BRIGHT";
    public override string Icon => "✨";
    public override string Description => "Clear, present voice: presence boost, air, de-esser, compression.";
    public override ParamDef[] Params { get; } =
    {
        new("presence", "Presence (3 kHz)", 0, 9, 5f, "dB"),
        new("air", "Air (8 kHz)", 0, 9, 5, "dB"),
        new("deess", "De-esser", 0, 1, 0.6f),
        new("comp", "Compression", 0, 1, 0.5f),
    };

    protected override void OnPrepare() { _comp.Prepare(SampleRate, 6, 110); _de.Prepare(SampleRate); _hp.HighPass(SampleRate, 120); _thin.Peak(SampleRate, 260, 0.8, -2.5); }
    public override void Reset() { _hp.Reset(); _pres.Reset(); _air.Reset(); _thin.Reset(); _comp.Reset(); _de.Prepare(SampleRate); }

    protected override void ProcessCore(Span<float> buf, float i, bool dirty)
    {
        if (dirty)
        {
            _pres.Peak(SampleRate, 3200, 0.9, Values[0]);
            _air.HighShelf(SampleRate, 8000, Values[1]);
            _comp.Set(-21f, 1f + 3f * Values[3], 4f * Values[3]);
        }
        float deAmt = Values[2];
        for (int k = 0; k < buf.Length; k++)
        {
            float x = buf[k];
            float y = _thin.Process(_hp.Process(x));
            y = _pres.Process(y);
            y = _comp.Process(y);
            y = _air.Process(y);
            y = _de.Process(y, 0.05f, deAmt);
            buf[k] = x + (y - x) * i;
        }
    }
}

/// <summary>4 — Telephone / Vintage Radio: band-limited, saturated, optional hiss.</summary>
public sealed class TelephoneEffect : VoiceEffect
{
    readonly Biquad _hp1 = new(), _hp2 = new(), _lp1 = new(), _lp2 = new(), _mid = new(), _nbp = new();
    uint _rng = 22222;
    float _drive = 1.5f, _norm = 1f, _noiseAmp;

    public override string Id => "phone";
    public override string Name => "TELEPHONE";
    public override string Icon => "📞";
    public override string Description => "Band-limited vintage radio / phone with mild saturation and optional hiss.";
    public override ParamDef[] Params { get; } =
    {
        new("low", "Low cut", 200, 600, 350, "Hz"),
        new("high", "High cut", 2000, 5000, 3200, "Hz"),
        new("drive", "Saturation", 0, 1, 0.35f),
        new("noise", "Noise on (0/1)", 0, 1, 1),
        new("noiselvl", "Noise level", -60, -20, -44, "dB"),
    };

    protected override void OnPrepare() { }
    public override void Reset() { _hp1.Reset(); _hp2.Reset(); _lp1.Reset(); _lp2.Reset(); _mid.Reset(); _nbp.Reset(); }

    float Noise() { _rng = _rng * 1664525u + 1013904223u; return ((_rng >> 8) * (1f / 8388608f)) - 1f; }

    protected override void ProcessCore(Span<float> buf, float i, bool dirty)
    {
        if (dirty)
        {
            _hp1.HighPass(SampleRate, Values[0]); _hp2.HighPass(SampleRate, Values[0]);
            _lp1.LowPass(SampleRate, Values[1]); _lp2.LowPass(SampleRate, Values[1]);
            _mid.Peak(SampleRate, 1400, 1.0, 4);
            _nbp.BandPass(SampleRate, 2200, 0.5);
            _drive = 1f + 5f * Values[2]; _norm = 1f / MathF.Tanh(_drive);
            _noiseAmp = Values[3] >= 0.5f ? DspMath.DbToLin(Values[4]) * 2f : 0f;
        }
        for (int k = 0; k < buf.Length; k++)
        {
            float x = buf[k];
            float y = _lp2.Process(_lp1.Process(_mid.Process(_hp2.Process(_hp1.Process(x)))));
            y = MathF.Tanh(y * _drive * 1.6f) * _norm * 0.8f;
            if (_noiseAmp > 0f) y += _nbp.Process(Noise()) * _noiseAmp;
            buf[k] = x + (y - x) * i;
        }
    }
}

/// <summary>5 — Robot / Electronic: ring modulation, metallic comb, distortion.</summary>
public sealed class RobotEffect : VoiceEffect
{
    readonly Biquad _hp = new(), _lp = new();
    readonly DelayLine _comb = new();
    double _phase;
    float _metal, _drive = 2f, _norm = 1f;
    double _inc;

    public override string Id => "robot";
    public override string Name => "ROBOT";
    public override string Icon => "🤖";
    public override string Description => "Ring modulator + metallic comb + distortion. Live, no samples.";
    public override ParamDef[] Params { get; } =
    {
        new("modhz", "Modulation frequency", 20, 400, 70, "Hz"),
        new("metal", "Metallic resonance", 0, 1, 0.5f),
        new("drive", "Distortion", 0, 1, 0.4f),
    };

    protected override void OnPrepare() { _comb.Prepare(SampleRate / 100 + 4, Math.Max(1, SampleRate / 250)); _hp.HighPass(SampleRate, 120); _lp.LowPass(SampleRate, 6500); }
    public override void Reset() { _hp.Reset(); _lp.Reset(); _comb.Reset(); _phase = 0; }

    protected override void ProcessCore(Span<float> buf, float i, bool dirty)
    {
        if (dirty)
        {
            _inc = 2 * Math.PI * Values[0] / SampleRate;
            _metal = Values[1] * 0.85f;
            _drive = 1f + 6f * Values[2]; _norm = 1f / MathF.Tanh(_drive);
        }
        for (int k = 0; k < buf.Length; k++)
        {
            float x = buf[k];
            float y = _hp.Process(x);
            y *= (float)Math.Sin(_phase); _phase += _inc; if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            y = y + _metal * _comb.Process(y);
            y = MathF.Tanh(y * _drive * 2.2f) * _norm * 0.7f;
            y = _lp.Process(y);
            buf[k] = x + (y - x) * i;
        }
    }
}

/// <summary>6 — Alien / Character: pitch up with warble, formant-style resonances, shimmer, soft distortion.</summary>
public sealed class AlienEffect : VoiceEffect
{
    readonly PitchShifter _ps = new();
    readonly Biquad _hp = new(), _res1 = new(), _res2 = new(), _lp = new();
    readonly DelayLine _dry = new();
    Smoother _wet;
    double _lfo, _lfoInc, _ring, _ringInc;
    float _depth, _shimmer, _semi;

    public override string Id => "alien";
    public override string Name => "ALIEN";
    public override string Icon => "👽";
    public override string Description => "Pitch-up warble, resonant formants and shimmer — very different from Robot.";
    public override ParamDef[] Params { get; } =
    {
        new("pitch", "Pitch shift", 2, 12, 6, "st"),
        new("warble", "Warble rate", 0.5f, 12, 5.5f, "Hz"),
        new("wdepth", "Warble depth", 0, 1, 0.5f),
        new("shimmer", "Shimmer", 0, 1, 0.35f),
    };
    public override int LatencySamples => _ps.LatencySamples;

    protected override void OnPrepare()
    {
        _ps.Prepare(SampleRate, 30); _dry.Prepare(_ps.LatencySamples + 8, _ps.LatencySamples);
        _hp.HighPass(SampleRate, 160); _lp.LowPass(SampleRate, 8500);
        _wet.Init(0, 20, SampleRate);
    }
    public override void Reset() { _ps.Reset(); _hp.Reset(); _res1.Reset(); _res2.Reset(); _lp.Reset(); _dry.Reset(); _lfo = _ring = 0; }

    protected override void ProcessCore(Span<float> buf, float i, bool dirty)
    {
        if (dirty)
        {
            _semi = Values[0] * i; _depth = Values[2]; _shimmer = Values[3] * 0.5f;
            _lfoInc = 2 * Math.PI * Values[1] / SampleRate;
            _ringInc = 2 * Math.PI * 1100.0 / SampleRate;
            _res1.Peak(SampleRate, 1800, 2.5, 7); _res2.Peak(SampleRate, 3300, 3.0, -4);
            _wet.Set(MathF.Min(1f, i * 4f));
        }
        for (int k = 0; k < buf.Length; k++)
        {
            float x = buf[k];
            float d = _dry.Process(x);
            float st = _semi + (float)Math.Sin(_lfo) * _depth * 1.5f; _lfo += _lfoInc; if (_lfo > 2 * Math.PI) _lfo -= 2 * Math.PI;
            float y = _ps.Process(x, MathF.Pow(2f, st / 12f));
            y = _res2.Process(_res1.Process(_hp.Process(y)));
            float r = y * (float)Math.Sin(_ring); _ring += _ringInc; if (_ring > 2 * Math.PI) _ring -= 2 * Math.PI;
            y = y * (1f - _shimmer) + r * _shimmer * 1.4f;
            y = MathF.Tanh(y * 1.8f) * 0.65f;
            y = _lp.Process(y);
            float w = _wet.Next();
            buf[k] = d + (y - d) * w;
        }
    }
}
