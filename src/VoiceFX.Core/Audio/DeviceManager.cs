using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VoiceFX.Core.Audio;

public sealed record DeviceInfo(string Id, string Name, bool IsVirtual, bool IsDefault)
{
    public override string ToString() => IsVirtual ? Name + "  (virtual)" : Name;
}

public interface IAudioDeviceManager : IDisposable
{
    IReadOnlyList<DeviceInfo> GetInputs();
    IReadOnlyList<DeviceInfo> GetOutputs();
    DeviceInfo? FindVirtualOutput();
    MMDevice? Open(string? id, DataFlow flow);
    event Action? DevicesChanged;
}

/// <summary>Discovers capture/render endpoints and reports hot-plug changes.</summary>
public sealed class AudioDeviceManager : IAudioDeviceManager, IMMNotificationClient
{
    static readonly string[] VirtualMarkers = { "CABLE", "VB-Audio", "VoiceMeeter", "Virtual", "VAC", "Hi-Fi" };
    readonly MMDeviceEnumerator _enum = new();
    readonly IDiagnosticsService _log;
    public event Action? DevicesChanged;

    public AudioDeviceManager(IDiagnosticsService log)
    {
        _log = log;
        try { _enum.RegisterEndpointNotificationCallback(this); } catch (Exception ex) { _log.Warn("Device notifications unavailable: " + ex.Message); }
    }

    public static bool LooksVirtual(string name) => VirtualMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));

    IReadOnlyList<DeviceInfo> List(DataFlow flow)
    {
        var list = new List<DeviceInfo>();
        try
        {
            string? defId = null;
            try { defId = _enum.GetDefaultAudioEndpoint(flow, Role.Console).ID; } catch { }
            foreach (var d in _enum.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (d) list.Add(new DeviceInfo(d.ID, d.FriendlyName, LooksVirtual(d.FriendlyName), d.ID == defId));
            }
        }
        catch (Exception ex) { _log.Error("Device enumeration failed", ex); }
        return list;
    }

    /// <summary>Microphone list. The virtual cable's own capture side is hidden to prevent feedback loops.</summary>
    public IReadOnlyList<DeviceInfo> GetInputs() => List(DataFlow.Capture).Where(d => !(d.IsVirtual && d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase))).ToList();
    public IReadOnlyList<DeviceInfo> GetOutputs() => List(DataFlow.Render);

    /// <summary>Finds a VB-CABLE style render endpoint ("CABLE Input"), the entry point of the virtual microphone.</summary>
    public DeviceInfo? FindVirtualOutput()
    {
        var outs = GetOutputs();
        return outs.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?? outs.FirstOrDefault(d => d.IsVirtual);
    }

    public MMDevice? Open(string? id, DataFlow flow)
    {
        try
        {
            if (string.IsNullOrEmpty(id)) return _enum.GetDefaultAudioEndpoint(flow, Role.Console);
            var d = _enum.GetDevice(id);
            return d.State == DeviceState.Active ? d : null;
        }
        catch (Exception ex) { _log.Warn($"Open device failed ({flow}): {ex.Message}"); return null; }
    }

    void Raise() { try { DevicesChanged?.Invoke(); } catch { } }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => Raise();
    public void OnDeviceAdded(string pwstrDeviceId) => Raise();
    public void OnDeviceRemoved(string deviceId) => Raise();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => Raise();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enum.UnregisterEndpointNotificationCallback(this); } catch { }
        _enum.Dispose();
    }
}
