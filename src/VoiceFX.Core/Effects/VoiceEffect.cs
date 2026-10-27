using VoiceFX.Core.Dsp;

namespace VoiceFX.Core.Effects;

public sealed record ParamDef(string Key, string Label, float Min, float Max, float Default, string Unit = "");

/// <summary>
/// A voice-effect chain. All state is pre-allocated in Initialize; Process never allocates or locks.
/// Parameter values are plain floats written by the UI thread and read by the audio thread.
/// </summary>
public abstract class VoiceEffect
{
    float[] _last = Array.Empty<float>();
    float _lastIntensity = -1f;

    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Icon { get; }
    public abstract string Description { get; }
    public abstract ParamDef[] Params { get; }
    public float[] Values { get; private set; } = Array.Empty<float>();
    public int SampleRate { get; private set; } = 48000;
    public virtual int LatencySamples => 0;

    public void Initialize(int sampleRate)
    {
        SampleRate = sampleRate;
        if (Values.Length != Params.Length) Values = Params.Select(p => p.Default).ToArray();
        _last = new float[Values.Length];
        Array.Fill(_last, float.NaN);
        _lastIntensity = -1f;
        OnPrepare();
        Reset();
    }

    public void SetValue(int index, float value)
    {
        if ((uint)index >= (uint)Values.Length) return;
        var p = Params[index];
        Values[index] = Math.Clamp(value, p.Min, p.Max);
    }

    public void SetValues(float[]? values)
    {
        if (values == null) return;
        for (int i = 0; i < Math.Min(values.Length, Params.Length); i++) if (float.IsFinite(values[i])) SetValue(i, values[i]);
    }

    public void ResetToDefaults() { for (int i = 0; i < Params.Length; i++) Values[i] = Params[i].Default; }

    public void Process(Span<float> buffer, float intensity)
    {
        bool dirty = intensity != _lastIntensity;
        _lastIntensity = intensity;
        for (int i = 0; i < Values.Length; i++)
            if (_last[i] != Values[i]) { _last[i] = Values[i]; dirty = true; }
        ProcessCore(buffer, intensity, dirty);
    }

    protected abstract void OnPrepare();
    public abstract void Reset();
    /// <param name="dirty">true when any parameter or the intensity changed since the previous block.</param>
    protected abstract void ProcessCore(Span<float> buffer, float intensity, bool dirty);
}
