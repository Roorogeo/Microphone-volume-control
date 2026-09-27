using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using MicBoost.Core;

namespace MicBoost.Audio;

public sealed record CaptureDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates capture endpoints and reports hot-plug / default-device changes.
///
/// IMMNotificationClient callbacks arrive on a Core Audio worker thread and must return
/// quickly without calling back into the enumerator, so each callback only posts to the
/// UI thread, where a short debounce timer coalesces bursts (a USB mic plug-in fires
/// several events) into one <see cref="DevicesChanged"/> event.
/// </summary>
public sealed class AudioDeviceManager : IMMNotificationClient, IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _debounce;
    private bool _registered;

    public MMDeviceEnumerator Enumerator { get; } = new();

    /// <summary>Raised on the UI thread after devices were added/removed or the default changed.</summary>
    public event EventHandler? DevicesChanged;

    public AudioDeviceManager(SynchronizationContext uiContext)
    {
        _ui = uiContext;
        _debounce = new System.Windows.Forms.Timer { Interval = 300 };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        };
        _registered = Enumerator.RegisterEndpointNotificationCallback(this) == 0;
        if (!_registered) Log.Warn("Could not register for device notifications.");
    }

    public List<CaptureDeviceInfo> GetCaptureDevices()
    {
        var list = new List<CaptureDeviceInfo>();
        var defaultId = GetDefaultCaptureId();
        try
        {
            foreach (var device in Enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new CaptureDeviceInfo(device.ID, SafeName(device),
                        string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Enumerating capture devices failed: {ex.Message}");
        }
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string? GetDefaultCaptureId()
    {
        try
        {
            if (!Enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
            using var device = Enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return device.ID;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens an active device by ID, or null if it is gone/disabled.</summary>
    public MMDevice? TryGetDevice(string id)
    {
        try
        {
            var device = Enumerator.GetDevice(id);
            if (device.State == DeviceState.Active) return device;
            device.Dispose();
        }
        catch
        {
            // Not present.
        }
        return null;
    }

    public static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch { return "Microphone"; }
    }

    private void Schedule() => _ui.Post(_ =>
    {
        // Restart the debounce window on every event.
        _debounce.Stop();
        _debounce.Start();
    }, null);

    // --- IMMNotificationClient (called on a Core Audio thread) -------------------------

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => Schedule();
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => Schedule();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => Schedule();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture && role == Role.Console) Schedule();
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Property changes (names, formats, ...) are frequent and irrelevant here.
    }

    public void Dispose()
    {
        _debounce.Dispose();
        if (_registered)
        {
            try { Enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            _registered = false;
        }
        Enumerator.Dispose();
    }
}
