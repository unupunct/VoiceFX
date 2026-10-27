using System.Drawing;
using System.Windows.Forms;
using VoiceFX.Core.Effects;

namespace VoiceFX;

public interface ITrayIconService : IDisposable
{
    void Attach(MainWindow window, IReadOnlyList<VoiceEffect> presets);
    void SetVisible(bool visible);
}

/// <summary>System-tray icon: show window, mute, bypass, switch preset, exit.</summary>
public sealed class TrayIconService : ITrayIconService
{
    NotifyIcon? _icon;
    MainWindow? _win;
    readonly List<ToolStripMenuItem> _presetItems = new();
    ToolStripMenuItem? _mute, _bypass;

    public void Attach(MainWindow window, IReadOnlyList<VoiceEffect> presets)
    {
        _win = window;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show VoiceFX", null, (_, _) => window.ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        for (int i = 0; i < presets.Count; i++)
        {
            int idx = i;
            var it = new ToolStripMenuItem(presets[i].Name) { CheckOnClick = false };
            it.Click += (_, _) => window.SelectPreset(idx);
            _presetItems.Add(it); menu.Items.Add(it);
        }
        menu.Items.Add(new ToolStripSeparator());
        _mute = new ToolStripMenuItem("Mute"); _mute.Click += (_, _) => window.ToggleMute(); menu.Items.Add(_mute);
        _bypass = new ToolStripMenuItem("Bypass"); _bypass.Click += (_, _) => window.ToggleBypass(); menu.Items.Add(_bypass);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => window.ExitApp());
        menu.Opening += (_, _) =>
        {
            for (int i = 0; i < _presetItems.Count; i++) _presetItems[i].Checked = i == window.CurrentPreset;
            _mute!.Checked = window.IsMuted; _bypass!.Checked = window.IsBypass;
        };

        Icon? ico = null;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/voicefx.ico");
            using var s = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (s != null) ico = new Icon(s);
        }
        catch { }
        _icon = new NotifyIcon { Icon = ico ?? SystemIcons.Application, Text = "VoiceFX Studio", ContextMenuStrip = menu, Visible = false };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) window.ToggleVisibility(); };
    }

    public void SetVisible(bool visible) { if (_icon != null) _icon.Visible = visible; }

    public void Dispose() { if (_icon != null) { _icon.Visible = false; _icon.Dispose(); _icon = null; } }
}
