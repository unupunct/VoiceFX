using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VoiceFX.Core;
using VoiceFX.Core.Audio;
using VoiceFX.Core.Effects;

namespace VoiceFX;

public partial class MainWindow : Window
{
    readonly IAudioPipeline _pipe;
    readonly IVoicePresetManager _presets;
    readonly IAudioDeviceManager _devices;
    readonly ISettingsService _settingsSvc;
    readonly IDiagnosticsService _log;
    readonly IStartupService _startup;
    readonly ITrayIconService _tray;
    AppSettings S => _settingsSvc.Current;

    readonly List<ToggleButton> _presetButtons = new();
    readonly List<(TextBlock icon, TextBlock name)> _presetLabels = new();
    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    readonly DispatcherTimer _devTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool _loading = true, _exiting, _initialized;
    float _inLevel, _outLevel; int _tick; DateTime _clipUntil;
    static readonly DeviceInfo AutoInput = new("", "Default microphone", false, false);
    DeviceInfo AutoOutput => new("", _devices.FindVirtualOutput() is { } v ? $"Default: {v.Name} (VB-CABLE)" : "Default: VB-CABLE CABLE Input (not installed)", false, false);
    static readonly DeviceInfo AutoMonitor = new("", "Default output device", false, false);

    public int CurrentPreset => _pipe.Processing.PresetIndex;
    public bool IsMuted => _pipe.Processing.Muted;
    public bool IsBypass => _pipe.Processing.Bypass;

    public MainWindow(IAudioPipeline pipe, IVoicePresetManager presets, IAudioDeviceManager devices, ISettingsService settings,
                      IDiagnosticsService log, IStartupService startup, ITrayIconService tray)
    {
        _pipe = pipe; _presets = presets; _devices = devices; _settingsSvc = settings; _log = log; _startup = startup; _tray = tray;
        InitializeComponent();
        BuildPresetButtons();
        for (int ms = 10; ms <= 40; ms += 10) CmbBuffer.Items.Add(ms);
    }

    // ------------------------------------------------------------------ init
    public void Initialize()
    {
        if (_initialized) return; _initialized = true;
        _presets.LoadParams(S.PresetParams);
        new WindowInteropHelper(this).EnsureHandle();
        ApplySettingsToUi(includeWindow: true);

        _pipe.StateChanged += (_, _) => Dispatcher.BeginInvoke(UpdateStatus);
        _devices.DevicesChanged += () => Dispatcher.BeginInvoke(() => { _devTimer.Stop(); _devTimer.Start(); });
        _devTimer.Tick += (_, _) => { _devTimer.Stop(); RefreshDevices(); UpdateStatus(); };
        _ui.Tick += (_, _) => OnUiTick(); _ui.Start();
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _tray.Attach(this, _presets.Presets);
        _tray.SetVisible(S.TrayEnabled);
        StartEngine();
        _loading = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        Native.MakeNoActivate(h); Native.DarkTitleBar(h);
    }

    void BuildPresetButtons()
    {
        var list = _presets.Presets;
        for (int i = 0; i < list.Count; i++)
        {
            int idx = i;
            var icon = new TextBlock { Text = list[i].Icon, FontFamily = new FontFamily("Segoe UI Emoji"), HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White };
            var name = new TextBlock { Text = list[i].Name, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
            var btn = new ToggleButton
            {
                Style = (Style)FindResource("PresetButton"), Margin = new Thickness(4), ToolTip = list[i].Description,
                Content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { icon, name } }
            };
            btn.Click += (_, _) => SelectPreset(idx);
            PresetGrid.Children.Add(btn); _presetButtons.Add(btn); _presetLabels.Add((icon, name));
        }
    }

    void ApplySettingsToUi(bool includeWindow)
    {
        bool was = _loading; _loading = true;
        if (includeWindow)
        {
            Width = Math.Max(MinWidth, S.WindowWidth); Height = Math.Max(MinHeight, S.WindowHeight);
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (double.IsNaN(S.WindowLeft) || !vs.Contains(new Point(S.WindowLeft + 40, S.WindowTop + 20)))
            { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            else { WindowStartupLocation = WindowStartupLocation.Manual; Left = S.WindowLeft; Top = S.WindowTop; }
        }
        Topmost = S.AlwaysOnTop; BtnPin.IsChecked = S.AlwaysOnTop;
        SldIntensity.Value = S.Intensity * 100; TxtIntensity.Text = $"{S.Intensity * 100:F0}%";
        SldInGain.Value = S.InputGainDb; SldMaster.Value = S.MasterGainDb; SldGate.Value = S.GateThresholdDb;
        ChkGate.IsChecked = S.GateEnabled; ChkNr.IsChecked = S.NoiseReduction;
        CmbBuffer.SelectedItem = S.BufferMs;
        if (CmbBuffer.SelectedItem == null) CmbBuffer.SelectedItem = 20;
        ChkTray.IsChecked = S.TrayEnabled; ChkMinTray.IsChecked = S.MinimizeToTray; ChkCloseTray.IsChecked = S.CloseToTray;
        ChkStartMin.IsChecked = S.StartMinimized; ChkStartup.IsChecked = _startup.IsEnabled;
        ChkMonitor.IsChecked = false; // monitoring never starts enabled (feedback safety)
        var e = _pipe.Processing;
        e.Intensity = S.Intensity; e.InputGainDb = S.InputGainDb; e.MasterGainDb = S.MasterGainDb;
        e.GateEnabled = S.GateEnabled; e.GateThresholdDb = S.GateThresholdDb; e.NoiseReduction = S.NoiseReduction;
        e.Muted = false; e.Bypass = false; BtnMute.IsChecked = false; BtnBypass.IsChecked = false;
        e.PresetIndex = Math.Clamp(S.LastPreset, 0, _presets.Presets.Count - 1);
        UpdatePresetSelection();
        UpdateGainLabels();
        RefreshDevices();
        ApplyCompact(S.Compact, initial: true);
        _loading = was;
    }

    // ------------------------------------------------------------------ engine / devices
    PipelineConfig BuildConfig()
    {
        static string? Id(ComboBox c) { var id = (c.SelectedItem as DeviceInfo)?.Id; return string.IsNullOrEmpty(id) ? null : id; }
        return new PipelineConfig(Id(CmbInput), Id(CmbOutput), S.BufferMs);
    }

    void StartEngine() { var cfg = BuildConfig(); Task.Run(() => _pipe.Start(cfg)); }

    void RefreshDevices()
    {
        bool was = _loading; _loading = true;
        Fill(CmbInput, AutoInput, _devices.GetInputs(), S.InputDeviceId);
        Fill(CmbOutput, AutoOutput, _devices.GetOutputs(), S.OutputDeviceId);
        var outs = _devices.GetOutputs().Where(d => !d.IsVirtual).ToList();
        Fill(CmbMonitor, AutoMonitor, outs, S.MonitorDeviceId);
        _loading = was;
    }

    static void Fill(ComboBox box, DeviceInfo auto, IReadOnlyList<DeviceInfo> items, string? savedId)
    {
        var current = (box.SelectedItem as DeviceInfo)?.Id ?? savedId ?? "";
        box.Items.Clear(); box.Items.Add(auto);
        foreach (var d in items) box.Items.Add(d);
        var sel = box.Items.Cast<DeviceInfo>().FirstOrDefault(d => d.Id == current);
        if (sel == null && !string.IsNullOrEmpty(current)) { sel = new DeviceInfo(current, "(unavailable device)", false, false); box.Items.Add(sel); }
        box.SelectedItem = sel ?? auto;
    }

    void OnDeviceChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.InputDeviceId = (CmbInput.SelectedItem as DeviceInfo)?.Id.NullIfEmpty();
        S.OutputDeviceId = (CmbOutput.SelectedItem as DeviceInfo)?.Id.NullIfEmpty();
        ChkMonitor.IsChecked = false;
        QueueSave(); StartEngine();
    }

    void OnBuffer(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbBuffer.SelectedItem is not int ms) return;
        S.BufferMs = ms; QueueSave(); StartEngine();
    }

    void OnRestartEngine(object s, RoutedEventArgs e) { ChkMonitor.IsChecked = false; StartEngine(); }

    // ------------------------------------------------------------------ presets
    public void SelectPreset(int i)
    {
        i = Math.Clamp(i, 0, _presets.Presets.Count - 1);
        _pipe.Processing.PresetIndex = i; S.LastPreset = i;
        UpdatePresetSelection(); QueueSave();
    }

    void UpdatePresetSelection()
    {
        int cur = _pipe.Processing.PresetIndex;
        for (int i = 0; i < _presetButtons.Count; i++) _presetButtons[i].IsChecked = i == cur;
        BuildParamsPanel(cur);
    }

    void BuildParamsPanel(int index)
    {
        var fx = _presets.Presets[index];
        PresetParamsTitle.Text = $"{fx.Name} PARAMETERS";
        ParamsPanel.Children.Clear();
        for (int p = 0; p < fx.Params.Length; p++)
        {
            int pi = p; var def = fx.Params[p];
            bool isSwitch = def.Max == 1 && def.Min == 0 && def.Label.Contains("(0/1)");
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            g.Children.Add(new TextBlock { Text = def.Label.Replace(" (0/1)", ""), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var val = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var sld = new Slider { Minimum = def.Min, Maximum = def.Max, Value = fx.Values[p] };
            if (isSwitch) { sld.IsSnapToTickEnabled = true; sld.TickFrequency = 1; }
            Grid.SetColumn(sld, 1); Grid.SetColumn(val, 2);
            string Fmt(double v) => isSwitch ? (v >= 0.5 ? "On" : "Off") : (def.Max - def.Min > 20 ? $"{v:F0}" : $"{v:F1}") + (def.Unit.Length > 0 ? " " + def.Unit : "");
            val.Text = Fmt(sld.Value);
            sld.ValueChanged += (_, ev) => { fx.SetValue(pi, (float)ev.NewValue); val.Text = Fmt(ev.NewValue); if (!_loading) { S.PresetParams = _presets.SaveParams(); QueueSave(); } };
            g.Children.Add(sld); g.Children.Add(val);
            ParamsPanel.Children.Add(g);
        }
    }

    void OnResetPreset(object s, RoutedEventArgs e)
    {
        _presets.ResetPreset(_pipe.Processing.PresetIndex);
        S.PresetParams = _presets.SaveParams(); QueueSave(); BuildParamsPanel(_pipe.Processing.PresetIndex);
    }

    // ------------------------------------------------------------------ controls
    void OnMute(object s, RoutedEventArgs e) { _pipe.Processing.Muted = BtnMute.IsChecked == true; UpdateStatus(); }
    void OnBypass(object s, RoutedEventArgs e) { _pipe.Processing.Bypass = BtnBypass.IsChecked == true; UpdateStatus(); }
    public void ToggleMute() => Dispatcher.Invoke(() => { BtnMute.IsChecked = !(BtnMute.IsChecked == true); OnMute(this, new RoutedEventArgs()); });
    public void ToggleBypass() => Dispatcher.Invoke(() => { BtnBypass.IsChecked = !(BtnBypass.IsChecked == true); OnBypass(this, new RoutedEventArgs()); });

    void OnIntensity(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        float v = (float)(SldIntensity.Value / 100.0);
        _pipe.Processing.Intensity = v; TxtIntensity.Text = $"{SldIntensity.Value:F0}%";
        if (!_loading) { S.Intensity = v; QueueSave(); }
    }

    void OnGain(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        var p = _pipe.Processing;
        p.InputGainDb = (float)SldInGain.Value; p.MasterGainDb = (float)SldMaster.Value; p.GateThresholdDb = (float)SldGate.Value;
        UpdateGainLabels();
        if (!_loading) { S.InputGainDb = p.InputGainDb; S.MasterGainDb = p.MasterGainDb; S.GateThresholdDb = p.GateThresholdDb; QueueSave(); }
    }
    void UpdateGainLabels()
    {
        TxtInGain.Text = $"{SldInGain.Value:+0.0;-0.0;0.0} dB"; TxtMaster.Text = $"{SldMaster.Value:+0.0;-0.0;0.0} dB"; TxtGate.Text = $"{SldGate.Value:F0} dB";
    }

    void OnToggles(object s, RoutedEventArgs e)
    {
        var p = _pipe.Processing;
        p.GateEnabled = ChkGate.IsChecked == true; p.NoiseReduction = ChkNr.IsChecked == true;
        S.GateEnabled = p.GateEnabled; S.NoiseReduction = p.NoiseReduction; QueueSave();
    }

    // Monitoring (hear yourself) — disabled by default; refuses the virtual output to prevent feedback.
    void OnMonitor(object s, RoutedEventArgs e) => ApplyMonitor();
    void OnMonitorDevice(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.MonitorDeviceId = (CmbMonitor.SelectedItem as DeviceInfo)?.Id.NullIfEmpty(); QueueSave();
        if (ChkMonitor.IsChecked == true) ApplyMonitor();
    }
    void ApplyMonitor()
    {
        bool on = ChkMonitor.IsChecked == true;
        var id = (CmbMonitor.SelectedItem as DeviceInfo)?.Id.NullIfEmpty();
        _pipe.SetMonitor(false, null);
        string? err = on ? _pipe.SetMonitor(true, id) : null;
        TxtMonitorErr.Text = err ?? "";
        if (err != null) ChkMonitor.IsChecked = false;
    }

    void OnAppToggles(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        S.TrayEnabled = ChkTray.IsChecked == true; S.MinimizeToTray = ChkMinTray.IsChecked == true;
        S.CloseToTray = ChkCloseTray.IsChecked == true; S.StartMinimized = ChkStartMin.IsChecked == true;
        S.StartWithWindows = ChkStartup.IsChecked == true;
        try { _startup.Set(S.StartWithWindows); } catch (Exception ex) { _log.Error("Startup registration failed", ex); }
        _tray.SetVisible(S.TrayEnabled); QueueSave();
    }

    /// <summary>First-run requirement: VB-CABLE is the default virtual microphone driver. Offer to install it if missing.</summary>
    public void CheckVirtualCable()
    {
        if (S.CablePromptDismissed || _devices.FindVirtualOutput() != null) return;
        var r = MessageBox.Show(this, "VoiceFX needs a virtual audio cable so other apps (OBS, Discord, browsers…) can use your processed voice as a microphone.\n\nVB-CABLE is the default and is free. It is NOT installed yet.\n\nYes = open the VB-CABLE download page\nNo = remind me next time\nCancel = don't ask again\n\nAfter installing, VoiceFX picks up “CABLE Input” automatically; you can still choose any other output in the Output list.", "VoiceFX Studio – virtual microphone setup", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
        if (r == MessageBoxResult.Yes) Open("https://vb-audio.com/Cable/");
        else if (r == MessageBoxResult.Cancel) { S.CablePromptDismissed = true; QueueSave(); }
    }

    void OnOpenCable(object s, RoutedEventArgs e) => Open("https://vb-audio.com/Cable/");
    void OnOpenSound(object s, RoutedEventArgs e) => Open("ms-settings:sound");
    void OnOpenLogs(object s, RoutedEventArgs e) => Open(System.IO.Path.GetDirectoryName(_log.LogPath)!);
    static void Open(string target) { try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { } }

    void OnResetAll(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset all VoiceFX settings and preset parameters to defaults?", "VoiceFX Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _presets.ResetAll(); _settingsSvc.ResetToDefaults();
        try { _startup.Set(false); } catch { }
        ApplySettingsToUi(includeWindow: true);
        StartEngine();
    }

    // ------------------------------------------------------------------ window behaviour
    void OnPin(object s, RoutedEventArgs e) { Topmost = BtnPin.IsChecked == true; S.AlwaysOnTop = Topmost; QueueSave(); }
    void OnSettings(object s, RoutedEventArgs e) { SettingsScroll.Visibility = BtnSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; UpdateScrollMax(); }
    void OnCompact(object s, RoutedEventArgs e) => ApplyCompact(BtnCompact.IsChecked == true, initial: false);

    void ApplyCompact(bool compact, bool initial)
    {
        if (!initial && compact != S.Compact)
        {
            if (compact) { S.WindowWidth = ActualWidth; S.WindowHeight = ActualHeight; }
            else { S.CompactWidth = ActualWidth; S.CompactHeight = ActualHeight; }
        }
        S.Compact = compact; BtnCompact.IsChecked = compact;
        var vis = compact ? Visibility.Collapsed : Visibility.Visible;
        DevicePanel.Visibility = vis; IntensityPanel.Visibility = vis; BtnSettings.Visibility = vis;
        if (compact) { SettingsScroll.Visibility = Visibility.Collapsed; BtnSettings.IsChecked = false; }
        StatusText.MaxWidth = compact ? 120 : 230;
        MinWidth = compact ? 200 : 300; MinHeight = compact ? 180 : 260;
        Width = compact ? Math.Max(MinWidth, S.CompactWidth) : Math.Max(MinWidth, S.WindowWidth);
        Height = compact ? Math.Max(MinHeight, S.CompactHeight) : Math.Max(MinHeight, S.WindowHeight);
        if (!initial) QueueSave();
    }

    void OnLayoutChanged(object s, SizeChangedEventArgs e)
    {
        double cell = Math.Min(PresetGrid.ActualWidth / 2, PresetGrid.ActualHeight / 3);
        double nameSize = Math.Clamp(cell * 0.13, 10, 26), iconSize = Math.Clamp(cell * 0.32, 16, 56);
        foreach (var (icon, name) in _presetLabels) { icon.FontSize = iconSize; name.FontSize = nameSize; }
        UpdateScrollMax();
    }
    void UpdateScrollMax() => SettingsScroll.MaxHeight = Math.Max(120, ActualHeight * 0.55);

    public void ShowFromTray()
    {
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        bool t = Topmost; Topmost = true; Topmost = t; // raise without taking focus
    }
    public void ToggleVisibility() { if (IsVisible && WindowState != WindowState.Minimized) Hide(); else ShowFromTray(); }
    public void ExitApp() { _exiting = true; Application.Current.Shutdown(); }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && S.MinimizeToTray && S.TrayEnabled) Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting && S.CloseToTray && S.TrayEnabled) { e.Cancel = true; Hide(); return; }
        SaveNow();
        _ui.Stop();
        try { _pipe.Stop(); } catch { }
        base.OnClosing(e);
    }

    void QueueSave() { if (_loading) return; _saveTimer.Stop(); _saveTimer.Start(); }
    void SaveNow()
    {
        if (WindowState == WindowState.Normal)
        {
            S.WindowLeft = Left; S.WindowTop = Top;
            if (S.Compact) { S.CompactWidth = ActualWidth; S.CompactHeight = ActualHeight; } else { S.WindowWidth = ActualWidth; S.WindowHeight = ActualHeight; }
        }
        S.PresetParams = _presets.SaveParams();
        _settingsSvc.Save();
    }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); QueueSave(); }
    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) { base.OnRenderSizeChanged(sizeInfo); QueueSave(); }

    // ------------------------------------------------------------------ live UI (meters, status)
    static float ToMeter(float lin) => lin <= 0.00001f ? 0f : Math.Clamp((20f * MathF.Log10(lin) + 60f) / 60f, 0f, 1f);

    void OnUiTick()
    {
        var (i, o, clip) = _pipe.Meters.Read();
        float li = ToMeter(i), lo = ToMeter(o);
        _inLevel = li > _inLevel ? li : _inLevel * 0.90f; _outLevel = lo > _outLevel ? lo : _outLevel * 0.90f;
        double w = ((FrameworkElement)InCover.Parent).ActualWidth;
        InCover.Width = (1 - _inLevel) * w; OutCover.Width = (1 - _outLevel) * w;
        if (clip) _clipUntil = DateTime.UtcNow.AddSeconds(1.5);
        bool lit = DateTime.UtcNow < _clipUntil;
        ClipLamp.Background = lit ? Brushes.Red : (Brush)new BrushConverter().ConvertFromString("#2A1517")!;
        ClipText.Foreground = lit ? Brushes.White : (Brush)new BrushConverter().ConvertFromString("#6E3B3E")!;
        if (++_tick % 15 == 0) UpdateStatus();
    }

    void UpdateStatus()
    {
        var st = _pipe.State;
        Brush dot = st switch { EngineState.Running => Brushes.LimeGreen, EngineState.Error => Brushes.Red, EngineState.Stopped => Brushes.Gray, _ => Brushes.Orange };
        StatusDot.Fill = dot;
        string mode = IsMuted ? "MUTED" : IsBypass ? "BYPASS" : _presets.Presets[CurrentPreset].Name;
        StatusText.Text = st switch
        {
            EngineState.Running => $"ACTIVE · {mode}",
            EngineState.Starting => "Starting…",
            EngineState.Recovering => "Reconnecting…",
            EngineState.Error => "Device error",
            _ => "Stopped"
        };
        StatusText.ToolTip = _pipe.StatusMessage;

        string text; string bg;
        if (st is EngineState.Error or EngineState.Recovering) { text = _pipe.StatusMessage + " – retrying automatically."; bg = "#3A1517"; }
        else if (st == EngineState.Running && _pipe.OutputConnected && _pipe.OutputIsVirtual)
        {
            string mic = (_pipe.OutputName ?? "").Replace("Input", "Output");
            text = $"Virtual mic ready → in your apps choose “{mic}” as the microphone."; bg = "#10301A";
        }
        else if (st == EngineState.Running && _pipe.OutputConnected) { text = "Output is a normal device, not a virtual microphone – other apps cannot use it as a mic. Install VB-CABLE and select “CABLE Input”."; bg = "#2A2313"; }
        else if (st == EngineState.Running) { text = "No virtual microphone found. Install VB-CABLE (Settings ▸ Virtual microphone), then select “CABLE Input” as Output."; bg = "#2A2313"; }
        else { text = _pipe.StatusMessage; bg = "#2A2313"; }
        BannerText.Text = text; Banner.Background = (Brush)new BrushConverter().ConvertFromString(bg)!;
        if (st == EngineState.Running)
            TxtLatency.Text = $"Estimated end-to-end latency: {_pipe.EstimatedLatencyMs:F0} ms · {_pipe.SampleRate / 1000.0:F1} kHz · underruns: {_pipe.Underruns}";
    }
}

static class StringExt { public static string? NullIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s; }
