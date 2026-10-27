using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using VoiceFX.Core;
using VoiceFX.Core.Audio;

namespace VoiceFX;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "VoiceFX.Studio.SingleInstance", out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "VoiceFX.Studio.Show");
        if (!first) { _showEvent.Set(); Shutdown(); return; }

        var sc = new ServiceCollection();
        sc.AddSingleton<IDiagnosticsService, DiagnosticsService>();
        sc.AddSingleton<ISettingsService>(p => new SettingsService(p.GetRequiredService<IDiagnosticsService>()));
        sc.AddSingleton<IAudioDeviceManager, AudioDeviceManager>();
        sc.AddSingleton<IVoicePresetManager, VoicePresetManager>();
        sc.AddSingleton<IAudioPipeline, AudioPipeline>();
        sc.AddSingleton<IStartupService, StartupService>();
        sc.AddSingleton<ITrayIconService, TrayIconService>();
        sc.AddSingleton<MainWindow>();
        _services = sc.BuildServiceProvider();

        var log = _services.GetRequiredService<IDiagnosticsService>();
        log.Info($"VoiceFX Studio starting (v{typeof(App).Assembly.GetName().Version}, {Environment.OSVersion})");
        DispatcherUnhandledException += (_, ex) => { log.Error("Unhandled UI exception", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { log.Error("Unobserved task exception", ex.Exception); ex.SetObserved(); };

        var win = _services.GetRequiredService<MainWindow>();
        MainWindow = win;
        win.Initialize();
        var settings = _services.GetRequiredService<ISettingsService>().Current;
        bool hidden = (e.Args.Contains("--minimized") || settings.StartMinimized) && settings.TrayEnabled;
        if (!hidden) { win.Show(); win.Dispatcher.BeginInvoke(win.CheckVirtualCable, System.Windows.Threading.DispatcherPriority.ApplicationIdle); }

        new Thread(() =>
        {
            while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(() => win.ShowFromTray());
        }) { IsBackground = true, Name = "ShowSignal" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _services?.Dispose(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
