using System.Diagnostics;
using Microsoft.Win32;

namespace VoiceFX;

public interface IStartupService
{
    bool IsEnabled { get; }
    void Set(bool enabled);
}

/// <summary>Launch-at-login via the per-user Run key (no admin rights, no scheduled task).</summary>
public sealed class StartupService : IStartupService
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "VoiceFXStudio";
    public bool IsEnabled
    {
        get { using var k = Registry.CurrentUser.OpenSubKey(KeyPath); return k?.GetValue(ValueName) != null; }
    }
    public void Set(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) k.SetValue(ValueName, $"\"{Process.GetCurrentProcess().MainModule?.FileName}\" --minimized");
        else k.DeleteValue(ValueName, false);
    }
}
