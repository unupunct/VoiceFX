using System.Text.Json;
using VoiceFX.Core.Effects;

namespace VoiceFX.Core;

// ---------------------------------------------------------------- Diagnostics
public interface IDiagnosticsService
{
    string LogPath { get; }
    IReadOnlyList<string> Recent { get; }
    event Action<string>? ErrorReported;
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>File + in-memory diagnostic log. Never records audio content.</summary>
public sealed class DiagnosticsService : IDiagnosticsService
{
    readonly object _lock = new();
    readonly Queue<string> _recent = new();
    public string LogPath { get; }
    public event Action<string>? ErrorReported;

    public DiagnosticsService(string? dir = null)
    {
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceFX", "logs");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "voicefx.log");
    }

    public IReadOnlyList<string> Recent { get { lock (_lock) return _recent.ToArray(); } }

    public void Info(string m) => Write("INFO ", m);
    public void Warn(string m) => Write("WARN ", m);
    public void Error(string m, Exception? ex = null)
    {
        Write("ERROR", ex == null ? m : $"{m}: {ex.GetType().Name}: {ex.Message}");
        ErrorReported?.Invoke(m);
    }

    void Write(string level, string m)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {m}";
        lock (_lock)
        {
            _recent.Enqueue(line); while (_recent.Count > 200) _recent.Dequeue();
            try
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 1_000_000) File.Move(LogPath, LogPath + ".old", true);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { /* logging must never throw */ }
        }
    }
}

// ---------------------------------------------------------------- Settings
public sealed class AppSettings
{
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 440;
    public double WindowHeight { get; set; } = 620;
    public double CompactWidth { get; set; } = 340;
    public double CompactHeight { get; set; } = 300;
    public bool AlwaysOnTop { get; set; } = true;
    public bool Compact { get; set; }

    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public string? MonitorDeviceId { get; set; }

    public int LastPreset { get; set; }
    public float Intensity { get; set; } = 1f;
    public float InputGainDb { get; set; }
    public float MasterGainDb { get; set; }
    public bool GateEnabled { get; set; }
    public float GateThresholdDb { get; set; } = -50f;
    public bool NoiseReduction { get; set; }
    public int BufferMs { get; set; } = 20;

    public bool TrayEnabled { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool CablePromptDismissed { get; set; }

    public Dictionary<string, float[]> PresetParams { get; set; } = new();
}

public interface ISettingsService
{
    AppSettings Current { get; }
    string Path { get; }
    void Save();
    void ResetToDefaults();
}

public sealed class SettingsService : ISettingsService
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    readonly IDiagnosticsService _log;
    public AppSettings Current { get; private set; } = new();
    public string Path { get; }

    public SettingsService(IDiagnosticsService log, string? dir = null)
    {
        _log = log;
        dir ??= System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceFX");
        Directory.CreateDirectory(dir);
        Path = System.IO.Path.Combine(dir, "settings.json");
        try
        {
            if (File.Exists(Path)) Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings();
        }
        catch (Exception ex) { _log.Warn($"Settings unreadable, using defaults ({ex.Message})"); Current = new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts));
            File.Move(tmp, Path, true);
        }
        catch (Exception ex) { _log.Error("Could not save settings", ex); }
    }

    public void ResetToDefaults() { Current = new AppSettings(); Save(); }
}

// ---------------------------------------------------------------- Presets
public interface IVoicePresetManager
{
    IReadOnlyList<VoiceEffect> Presets { get; }
    void Initialize(int sampleRate);
    void LoadParams(Dictionary<string, float[]> saved);
    Dictionary<string, float[]> SaveParams();
    void ResetAll();
    void ResetPreset(int index);
}

public sealed class VoicePresetManager : IVoicePresetManager
{
    public IReadOnlyList<VoiceEffect> Presets { get; } = new VoiceEffect[]
    {
        new NaturalEffect(), new DeepRadioEffect(), new BrightEffect(),
        new TelephoneEffect(), new RobotEffect(), new AlienEffect(),
    };

    public VoicePresetManager() { foreach (var p in Presets) p.Initialize(48000); }

    public void Initialize(int sampleRate) { foreach (var p in Presets) p.Initialize(sampleRate); }

    public void LoadParams(Dictionary<string, float[]> saved)
    {
        foreach (var p in Presets) if (saved.TryGetValue(p.Id, out var v)) p.SetValues(v);
    }

    public Dictionary<string, float[]> SaveParams() => Presets.ToDictionary(p => p.Id, p => (float[])p.Values.Clone());
    public void ResetAll() { foreach (var p in Presets) p.ResetToDefaults(); }
    public void ResetPreset(int i) => Presets[i].ResetToDefaults();
}
