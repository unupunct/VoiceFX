using NAudio.CoreAudioApi;

namespace VoiceFX.Core.Audio;

public enum EngineState { Stopped, Starting, Running, Recovering, Error }

public sealed record PipelineConfig(string? InputId, string? OutputId, int BufferMs);

public interface IAudioPipeline : IDisposable
{
    EngineState State { get; }
    string StatusMessage { get; }
    AudioProcessingEngine Processing { get; }
    IAudioMeterService Meters { get; }
    bool OutputConnected { get; }
    bool OutputIsVirtual { get; }
    string? OutputName { get; }
    bool MonitorEnabled { get; }
    long Underruns { get; }
    double EstimatedLatencyMs { get; }
    int SampleRate { get; }
    event Action<EngineState, string>? StateChanged;
    void Start(PipelineConfig config);
    void Stop();
    /// <summary>Starts/stops the optional monitor (hear-yourself) output. Returns an error text or null.</summary>
    string? SetMonitor(bool enabled, string? deviceId);
}

/// <summary>
/// Microphone → capture → DSP → router. Owns device lifetime, supervises failures and auto-recovers
/// when a device disappears or an audio stream stops.
/// </summary>
public sealed class AudioPipeline : IAudioPipeline
{
    readonly IAudioDeviceManager _devices;
    readonly IDiagnosticsService _log;
    readonly AudioMeterService _meters = new();
    readonly AudioCaptureEngine _capture = new();
    readonly VirtualMicrophoneRouter _router = new();
    readonly object _gate = new();
    Timer? _retry;
    PipelineConfig? _config;
    volatile bool _ready, _wantRunning;
    string? _outputId; bool _outputIsVirtual; string? _monitorId;
    int _captureBufMs = 10;
    bool _disposed;

    public AudioProcessingEngine Processing { get; }
    public IAudioMeterService Meters => _meters;
    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusMessage { get; private set; } = "Engine stopped";
    public bool OutputConnected => _router.IsConnected;
    public bool OutputIsVirtual => _outputIsVirtual;
    public string? OutputName => _router.VirtualDeviceName;
    public bool MonitorEnabled => _router.MonitorActive;
    public long Underruns => _router.Underruns;
    public int SampleRate => _capture.SampleRate;
    public double EstimatedLatencyMs => _ready
        ? _captureBufMs + _router.LatencyMs + Processing.LatencySamples * 1000.0 / Math.Max(1, Processing.SampleRate)
        : 0;
    public event Action<EngineState, string>? StateChanged;

    public AudioPipeline(IAudioDeviceManager devices, IVoicePresetManager presets, IDiagnosticsService log)
    {
        _devices = devices; _log = log;
        Processing = new AudioProcessingEngine(presets, _meters);
        _capture.Block += OnBlock;
        _capture.Stopped += ex => OnStreamFailure("Microphone stream stopped", ex);
        _router.OutputFailed += (m, ex) => OnStreamFailure(m, ex);
        _devices.DevicesChanged += OnDevicesChanged;
    }

    void SetState(EngineState s, string msg)
    {
        State = s; StatusMessage = msg;
        _log.Info($"Engine {s}: {msg}");
        StateChanged?.Invoke(s, msg);
    }

    void OnBlock(float[] data, int n)
    {
        if (!_ready) return;
        Processing.Process(data.AsSpan(0, n));
        _router.Write(data, n);
    }

    public void Start(PipelineConfig config)
    {
        lock (_gate)
        {
            _config = config; _wantRunning = true;
            StartInternal(initial: true);
        }
    }

    void StartInternal(bool initial)
    {
        if (_disposed) return;
        TeardownStreams();
        SetState(initial ? EngineState.Starting : EngineState.Recovering, initial ? "Starting audio engine…" : "Reconnecting…");
        var cfg = _config!;
        MMDevice? mic = null, outDev = null;
        try
        {
            mic = _devices.Open(cfg.InputId, DataFlow.Capture);
            if (mic == null) throw new InvalidOperationException(string.IsNullOrEmpty(cfg.InputId)
                ? "No microphone found" : "Selected microphone is unavailable (unplugged or disabled)");

            int sr = mic.AudioClient.MixFormat.SampleRate;
            Processing.Prepare(sr);
            _captureBufMs = Math.Max(10, Math.Min(cfg.BufferMs, 20));

            // Output: explicit selection, else a detected virtual cable, else none (never default speakers: feedback risk).
            string? outId = cfg.OutputId;
            DeviceInfo? vdev = null;
            if (string.IsNullOrEmpty(outId)) { vdev = _devices.FindVirtualOutput(); outId = vdev?.Id; }
            if (!string.IsNullOrEmpty(outId))
            {
                outDev = _devices.Open(outId, DataFlow.Render);
                if (outDev == null) throw new InvalidOperationException("Selected output device is unavailable");
                _outputId = outId; _outputIsVirtual = AudioDeviceManager.LooksVirtual(outDev.FriendlyName);
                _router.Start(outDev, sr, cfg.BufferMs);
            }
            else { _outputId = null; _outputIsVirtual = false; }

            _ready = true;
            _capture.Start(mic, _captureBufMs);
            string msg = outDev == null ? $"Running – no output selected / no virtual cable found ({mic.FriendlyName})"
                : _outputIsVirtual ? $"Running → {outDev.FriendlyName}"
                : $"Running → {outDev.FriendlyName} (not a virtual device)";
            SetState(EngineState.Running, msg);
            StopRetry();
        }
        catch (Exception ex)
        {
            _ready = false;
            TeardownStreams();
            _log.Error("Engine start failed", ex);
            SetState(EngineState.Error, ex.Message);
            ScheduleRetry();
        }
        finally { mic?.Dispose(); outDev?.Dispose(); }
    }

    void OnStreamFailure(string what, Exception? ex)
    {
        if (!_wantRunning || _disposed) return;
        if (ex == null && State != EngineState.Running) return; // normal stop
        lock (_gate)
        {
            if (!_wantRunning) return;
            _ready = false;
            _log.Warn($"{what}: {ex?.Message}");
            SetState(EngineState.Recovering, $"{what} – reconnecting…");
            ScheduleRetry();
        }
    }

    void OnDevicesChanged()
    {
        _log.Info("Audio devices changed");
        if (_wantRunning && State is EngineState.Error or EngineState.Recovering)
            ThreadPool.QueueUserWorkItem(_ => RetryNow());
    }

    void ScheduleRetry()
    {
        if (!_wantRunning) return;
        _retry?.Dispose();
        _retry = new Timer(_ => RetryNow(), null, 2000, 2000);
    }
    void StopRetry() { _retry?.Dispose(); _retry = null; }

    void RetryNow()
    {
        lock (_gate)
        {
            if (!_wantRunning || _disposed || State == EngineState.Running) return;
            StartInternal(initial: false);
        }
    }

    void TeardownStreams()
    {
        _ready = false;
        _capture.Stop();
        _router.Stop();
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wantRunning = false; StopRetry(); TeardownStreams();
            SetState(EngineState.Stopped, "Engine stopped");
        }
    }

    public string? SetMonitor(bool enabled, string? deviceId)
    {
        lock (_gate)
        {
            if (!enabled) { _router.StopMonitor(); return null; }
            if (!_ready) return "Start the engine first";
            using var dev = _devices.Open(deviceId, DataFlow.Render);
            if (dev == null) return "Monitor device unavailable";
            if (dev.ID == _outputId) return "Monitor device is the same as the virtual output; choose headphones/speakers";
            if (AudioDeviceManager.LooksVirtual(dev.FriendlyName)) return "Monitor device must not be a virtual cable";
            try { _router.StartMonitor(dev, _capture.SampleRate, _config?.BufferMs ?? 20); _monitorId = dev.ID; return null; }
            catch (Exception ex) { _log.Error("Monitor start failed", ex); return ex.Message; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _devices.DevicesChanged -= OnDevicesChanged;
        _capture.Dispose(); _router.Dispose();
    }
}
