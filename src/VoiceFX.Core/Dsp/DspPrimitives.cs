namespace VoiceFX.Core.Dsp;

public static class DspMath
{
    public static float DbToLin(float db) => MathF.Pow(10f, db / 20f);
    public static float LinToDb(float lin) => 20f * MathF.Log10(MathF.Max(lin, 1e-9f));
    public static float TimeCoef(float ms, int sr) => ms <= 0f ? 0f : MathF.Exp(-1f / (ms * 0.001f * sr));
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>One-pole parameter smoother (removes zipper noise / clicks).</summary>
public struct Smoother
{
    float _cur, _target, _coef;
    public void Init(float value, float ms, int sr) { _cur = _target = value; _coef = DspMath.TimeCoef(ms, sr); }
    public void Set(float target) => _target = target;
    public void Snap(float v) { _cur = _target = v; }
    public float Next() { _cur = _target + (_cur - _target) * _coef; return _cur; }
    public float Current => _cur;
}

/// <summary>Integer sample delay line.</summary>
public sealed class DelayLine
{
    float[] _buf = new float[1];
    int _w, _mask, _delay;
    public void Prepare(int maxDelay, int delay)
    {
        int size = 1; while (size < maxDelay + 2) size <<= 1;
        _buf = new float[size]; _mask = size - 1; _w = 0; _delay = delay;
    }
    public void SetDelay(int d) => _delay = Math.Clamp(d, 0, _mask - 1);
    public void Reset() { Array.Clear(_buf); _w = 0; }
    public float Process(float x)
    {
        _buf[_w] = x;
        float y = _buf[(_w - _delay) & _mask];
        _w = (_w + 1) & _mask;
        return y;
    }
}

/// <summary>RBJ biquad, double-precision state for stable low-frequency filters.</summary>
public sealed class Biquad
{
    double _b0 = 1, _b1, _b2, _a1, _a2, _z1, _z2;
    public void Reset() { _z1 = _z2 = 0; }

    public float Process(float x)
    {
        double xi = x;
        double y = _b0 * xi + _z1;
        _z1 = _b1 * xi - _a1 * y + _z2;
        _z2 = _b2 * xi - _a2 * y;
        return (float)y;
    }

    void Set(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    static void Common(int sr, double f, out double cos, out double sin, out double w)
    {
        f = Math.Clamp(f, 10, sr * 0.45);
        w = 2 * Math.PI * f / sr; cos = Math.Cos(w); sin = Math.Sin(w);
    }

    public void LowPass(int sr, double f, double q = 0.7071)
    {
        Common(sr, f, out var c, out var s, out _); double al = s / (2 * q);
        Set((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + al, -2 * c, 1 - al);
    }
    public void HighPass(int sr, double f, double q = 0.7071)
    {
        Common(sr, f, out var c, out var s, out _); double al = s / (2 * q);
        Set((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + al, -2 * c, 1 - al);
    }
    public void BandPass(int sr, double f, double q)
    {
        Common(sr, f, out var c, out var s, out _); double al = s / (2 * q);
        Set(al, 0, -al, 1 + al, -2 * c, 1 - al);
    }
    public void Peak(int sr, double f, double q, double gainDb)
    {
        Common(sr, f, out var c, out var s, out _); double A = Math.Pow(10, gainDb / 40), al = s / (2 * q);
        Set(1 + al * A, -2 * c, 1 - al * A, 1 + al / A, -2 * c, 1 - al / A);
    }
    public void LowShelf(int sr, double f, double gainDb)
    {
        Common(sr, f, out var c, out var s, out _); double A = Math.Pow(10, gainDb / 40);
        double be = 2 * Math.Sqrt(A) * (s / 2 * Math.Sqrt(2));
        Set(A * ((A + 1) - (A - 1) * c + be), 2 * A * ((A - 1) - (A + 1) * c), A * ((A + 1) - (A - 1) * c - be),
            (A + 1) + (A - 1) * c + be, -2 * ((A - 1) + (A + 1) * c), (A + 1) + (A - 1) * c - be);
    }
    public void HighShelf(int sr, double f, double gainDb)
    {
        Common(sr, f, out var c, out var s, out _); double A = Math.Pow(10, gainDb / 40);
        double be = 2 * Math.Sqrt(A) * (s / 2 * Math.Sqrt(2));
        Set(A * ((A + 1) + (A - 1) * c + be), -2 * A * ((A - 1) + (A + 1) * c), A * ((A + 1) + (A - 1) * c - be),
            (A + 1) - (A - 1) * c + be, 2 * ((A - 1) - (A + 1) * c), (A + 1) - (A - 1) * c - be);
    }
}

/// <summary>Feed-forward peak compressor.</summary>
public sealed class Compressor
{
    float _env, _att, _rel, _thr = -20, _ratio = 2, _makeup;
    public void Prepare(int sr, float attackMs, float releaseMs)
    { _att = DspMath.TimeCoef(attackMs, sr); _rel = DspMath.TimeCoef(releaseMs, sr); _env = 0; }
    public void Set(float thresholdDb, float ratio, float makeupDb) { _thr = thresholdDb; _ratio = MathF.Max(1f, ratio); _makeup = makeupDb; }
    public void Reset() => _env = 0;
    public float Process(float x)
    {
        float a = MathF.Abs(x);
        _env = a > _env ? _att * _env + (1 - _att) * a : _rel * _env + (1 - _rel) * a;
        float over = DspMath.LinToDb(_env) - _thr;
        float gr = over > 0 ? over * (1f - 1f / _ratio) : 0f;
        return x * DspMath.DbToLin(_makeup - gr);
    }
}

/// <summary>Look-ahead brick-wall style limiter with a final hard safety clamp.</summary>
public sealed class Limiter
{
    float[] _delay = new float[1], _gains = new float[1];
    int _idx, _len;
    float _gain = 1, _ceiling = 0.891f, _attCoef, _relCoef;
    public int LatencySamples => _len - 1;
    public float GainReductionDb => -DspMath.LinToDb(_gain);

    public void Prepare(int sr, float lookaheadMs = 1.5f, float releaseMs = 90f, float ceilingDb = -1f)
    {
        _len = Math.Max(2, (int)(sr * lookaheadMs / 1000f));
        _delay = new float[_len]; _gains = new float[_len];
        Array.Fill(_gains, 1f); _idx = 0; _gain = 1;
        _ceiling = DspMath.DbToLin(ceilingDb);
        _attCoef = DspMath.TimeCoef(lookaheadMs / 6f, sr);
        _relCoef = DspMath.TimeCoef(releaseMs, sr);
    }

    public float Process(float x)
    {
        float a = MathF.Abs(x);
        _delay[_idx] = x;
        _gains[_idx] = a > _ceiling ? _ceiling / a : 1f;
        float m = 1f;
        for (int i = 0; i < _len; i++) { float g = _gains[i]; if (g < m) m = g; }
        _idx++; if (_idx == _len) _idx = 0;
        _gain = m < _gain ? m + (_gain - m) * _attCoef : m + (_gain - m) * _relCoef;
        float y = _delay[_idx] * _gain;
        const float hard = 0.99f;
        return y > hard ? hard : (y < -hard ? -hard : y);
    }
}

/// <summary>Noise gate / downward expander with hold and hysteresis.</summary>
public sealed class NoiseGate
{
    float _env, _gain = 1, _envAtt, _envRel, _gAtt, _gRel;
    int _hold, _holdLen; bool _open = true;
    public void Prepare(int sr)
    {
        _envAtt = DspMath.TimeCoef(1f, sr); _envRel = DspMath.TimeCoef(60f, sr);
        _gAtt = DspMath.TimeCoef(4f, sr); _gRel = DspMath.TimeCoef(90f, sr);
        _holdLen = (int)(sr * 0.12f); _env = 0; _gain = 1; _open = true;
    }
    public float Process(float x, float thresholdDb, float floorDb)
    {
        float a = MathF.Abs(x);
        _env = a > _env ? _envAtt * _env + (1 - _envAtt) * a : _envRel * _env + (1 - _envRel) * a;
        float db = DspMath.LinToDb(_env);
        if (db > thresholdDb) { _open = true; _hold = _holdLen; }
        else if (db < thresholdDb - 4f) { if (_hold > 0) _hold--; else _open = false; }
        float target = _open ? 1f : DspMath.DbToLin(floorDb);
        _gain = target > _gain ? target + (_gain - target) * _gAtt : target + (_gain - target) * _gRel;
        return x * _gain;
    }
}

/// <summary>Split-band de-esser: only the sibilant band is attenuated.</summary>
public sealed class DeEsser
{
    readonly Biquad _bp = new(), _bp2 = new();
    float _env, _att, _rel;
    public void Prepare(int sr, float centerHz = 6500f)
    {
        _bp.BandPass(sr, centerHz, 1.1); _bp2.BandPass(sr, centerHz, 1.1);
        _att = DspMath.TimeCoef(1f, sr); _rel = DspMath.TimeCoef(40f, sr); _env = 0;
    }
    public float Process(float x, float threshold, float amount)
    {
        float band = _bp.Process(x);
        float a = MathF.Abs(band);
        _env = a > _env ? _att * _env + (1 - _att) * a : _rel * _env + (1 - _rel) * a;
        float g = _env > threshold ? threshold / _env : 1f;
        g = 1f - (1f - g) * amount;
        return x - band * (1f - g);
    }
}

/// <summary>
/// Time-domain pitch shifter (dual-tap crossfading delay line, 4-point Hermite interpolation).
/// Changes pitch without changing speaking speed. Ratio per sample allows vibrato.
/// </summary>
public sealed class PitchShifter
{
    float[] _buf = new float[1];
    int _w, _mask, _n;
    double _phase;
    const float MinDelay = 4f;
    public int LatencySamples => _n / 2 + (int)MinDelay;

    public void Prepare(int sr, float windowMs = 35f)
    {
        _n = (int)(sr * windowMs / 1000f);
        int size = 1; while (size < 2 * (_n + 16)) size <<= 1;
        _buf = new float[size]; _mask = size - 1; _w = 0; _phase = 0;
    }
    public void Reset() { Array.Clear(_buf); _w = 0; _phase = 0; }

    float Read(float delay)
    {
        float pos = _w - delay;
        int i = (int)MathF.Floor(pos); float f = pos - i;
        float y0 = _buf[(i - 1) & _mask], y1 = _buf[i & _mask], y2 = _buf[(i + 1) & _mask], y3 = _buf[(i + 2) & _mask];
        float c1 = 0.5f * (y2 - y0), c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3, c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);
        return ((c3 * f + c2) * f + c1) * f + y1;
    }

    public float Process(float x, float ratio)
    {
        _buf[_w] = x;
        float p1 = (float)_phase, p2 = p1 + 0.5f; if (p2 >= 1f) p2 -= 1f;
        float w1 = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * p1);
        float y = w1 * Read(MinDelay + p1 * _n) + (1f - w1) * Read(MinDelay + p2 * _n);
        _phase += (1.0 - ratio) / _n;
        if (_phase >= 1.0) _phase -= 1.0; else if (_phase < 0.0) _phase += 1.0;
        _w = (_w + 1) & _mask;
        return y;
    }
}
