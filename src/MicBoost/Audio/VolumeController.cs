using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using MicBoost.Core;

namespace MicBoost.Audio;

public enum SoftwareBoostMethod
{
    None,
    EqualizerApo,
    VirtualCable,
}

/// <summary>Why the requested boost could not be (fully) reached in software.</summary>
public enum SoftwareBoostProblem
{
    ApoNotInstalled,
    ApoNotEnabledForDevice,
    ApoSetupRequired,
}

/// <summary>Immutable snapshot of what is applied right now, for the UI.</summary>
public sealed record VolumeState(
    bool HasDevice,
    string? DeviceId,
    string DeviceName,
    double LevelPercent,
    bool Muted,
    float EndpointDb,
    float DesiredBoostDb,
    float HardwareBoostDb,
    float SoftwareBoostDb,
    SoftwareBoostMethod SoftwareMethod,
    bool BoostLimited)
{
    public float TotalBoostDb => HardwareBoostDb + SoftwareBoostDb;

    /// <summary>Overall gain relative to the endpoint's 0 dB reference.</summary>
    public float TotalDb => EndpointDb + TotalBoostDb;

    public bool IsBoosted => LevelPercent > 100.05;

    public static VolumeState NoDevice { get; } =
        new(false, null, "No microphone found", 0, false, 0, 0, 0, 0, SoftwareBoostMethod.None, false);
}

/// <summary>
/// Owns the selected microphone and turns a single 0..200 % slider value into device settings:
///
///   0..100 %   endpoint master volume (IAudioEndpointVolume scalar 0.0..1.0)
///   100..200 % endpoint at 100 %, plus a boost of (level - 100) / 100 * MaxBoostDb dB made up of
///              1. the hardware Microphone Boost (coarse dB steps, no CPU cost),
///              2. the remainder as software gain: Equalizer APO preamp, or else
///              3. the virtual-cable router's gain (if that fallback is enabled).
///
/// When a software stage is available the hardware step is rounded *down* and software fills
/// the gap exactly; without one the hardware step is rounded to the nearest supported value.
///
/// Also implements Lock Volume: endpoint change notifications carry an event-context GUID, so
/// changes made by MicBoost are recognised and everything else (Discord, Zoom, Teams, driver
/// auto-gain) is reverted to the user's level.
/// </summary>
public sealed class VolumeController : IDisposable
{
    /// <summary>Tags every volume change MicBoost makes, to tell them apart from other apps'.</summary>
    public static readonly Guid EventContext = new("6C1B1F4E-3A8A-4F6E-9A55-0D7C2B6F0B11");

    private const float LevelTolerance = 0.004f; // ~0.4 % - ignore rounding noise from drivers

    private readonly AppSettings _settings;
    private readonly AudioDeviceManager _devices;
    private readonly EqualizerApo _apo;
    private readonly SynchronizationContext _ui;
    private readonly System.Windows.Forms.Timer _restoreTimer;
    private readonly System.Windows.Forms.Timer _apoWriteTimer;
    private readonly LevelMonitor _monitor = new();
    private string? _lastApoContent;
    private bool _meteringRequested;
    private bool _monitorFailed;

    private MMDevice? _device;
    private AudioEndpointVolume? _endpoint;
    private double _level = 100;
    private bool _muted;

    public VolumeController(AppSettings settings, AudioDeviceManager devices, EqualizerApo apo, SynchronizationContext ui)
    {
        _settings = settings;
        _devices = devices;
        _apo = apo;
        _ui = ui;

        // Lock Volume restores after a short delay so a burst of changes (e.g. an app ramping
        // the level) results in one restore instead of a tug-of-war.
        _restoreTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _restoreTimer.Tick += (_, _) =>
        {
            _restoreTimer.Stop();
            if (_device == null) return;
            Log.Info($"Lock Volume: restoring {_level:0.#}% after an external change.");
            Apply();
            LevelRestored?.Invoke(this, EventArgs.Empty);
        };

        // Equalizer APO re-parses its whole configuration on every file change, so preamp
        // writes are debounced while the slider is being dragged.
        _apoWriteTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _apoWriteTimer.Tick += (_, _) =>
        {
            _apoWriteTimer.Stop();
            FlushApo();
        };

        Router.Failed += (_, reason) => _ui.Post(_ =>
        {
            RouterError = reason;
            Router.Stop();
            Apply();
        }, null);
    }

    public VolumeState State { get; private set; } = VolumeState.NoDevice;
    public MMDevice? Device => _device;
    public HardwareBoost? HardwareBoost { get; private set; }
    public string? HardwareBoostUnavailableReason { get; private set; }
    public VirtualCableRouter Router { get; } = new();
    public string? RouterError { get; private set; }

    public event EventHandler<VolumeState>? StateChanged;
    public event EventHandler<SoftwareBoostProblem>? SoftwareBoostUnavailable;
    public event EventHandler? SettingsDirty;
    public event EventHandler? DeviceLost;
    public event EventHandler? LevelRestored;

    // ---------------------------------------------------------------------------------
    // Device selection
    // ---------------------------------------------------------------------------------

    /// <summary>Switches to the given capture endpoint (null = no device available).</summary>
    public void SelectDevice(string? deviceId)
    {
        if (deviceId != null && _device != null &&
            string.Equals(_device.ID, deviceId, StringComparison.OrdinalIgnoreCase))
            return;

        Detach();
        if (deviceId == null)
        {
            Publish(VolumeState.NoDevice);
            return;
        }

        var device = _devices.TryGetDevice(deviceId);
        if (device == null)
        {
            Publish(VolumeState.NoDevice);
            return;
        }

        try
        {
            _device = device;
            _endpoint = device.AudioEndpointVolume;
            _endpoint.NotificationGuid = EventContext;
            _endpoint.OnVolumeNotification += OnVolumeNotification;
            _muted = _endpoint.Mute;

            OpenHardwareBoost();
            UpdateRouter();

            var name = AudioDeviceManager.SafeName(device);
            if (_settings.Devices.TryGetValue(device.ID, out var saved))
            {
                _level = saved.LevelPercent;
            }
            else
            {
                // First time we see this mic: adopt whatever it is set to right now.
                _level = DeriveLevelFromDevice();
            }
            _settings.GetDevice(device.ID).Name = name;
            Log.Info($"Selected '{name}' ({device.ID}), level {_level:0.#}%.");
            Apply();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Log.Warn($"Could not open device {deviceId}: {ex.Message}");
            Detach();
            Publish(VolumeState.NoDevice);
        }
    }

    private void Detach()
    {
        _restoreTimer.Stop();
        _monitor.Stop();
        _monitorFailed = false;
        Router.Stop();
        var endpoint = _endpoint;
        if (endpoint != null)
        {
            endpoint.OnVolumeNotification -= OnVolumeNotification;
            _endpoint = null;
        }
        if (HardwareBoost != null)
        {
            HardwareBoost.ChangedExternally -= OnHardwareBoostChangedExternally;
            HardwareBoost.Dispose();
            HardwareBoost = null;
        }
        if (_device != null)
        {
            try
            {
                _device.Dispose();
            }
            catch (Exception ex)
            {
                // NAudio's AudioEndpointVolume.Dispose throws if the device is already gone
                // (unregistering the callback fails). Its finalizer would call Dispose again
                // and throw on the finalizer thread, killing the process - so suppress it.
                Log.Warn($"Releasing device failed: {ex.Message}");
                GC.SuppressFinalize(_device);
                if (endpoint != null) GC.SuppressFinalize(endpoint);
            }
            _device = null;
        }
    }

    private void OpenHardwareBoost()
    {
        if (HardwareBoost != null)
        {
            HardwareBoost.ChangedExternally -= OnHardwareBoostChangedExternally;
            HardwareBoost.Dispose();
            HardwareBoost = null;
        }
        HardwareBoostUnavailableReason = null;
        if (_device == null) return;

        if (!_settings.UseHardwareBoost)
        {
            HardwareBoostUnavailableReason = "Disabled in settings.";
            return;
        }

        HardwareBoost = HardwareBoost.TryOpen(_device.ID, EventContext, out var reason);
        if (HardwareBoost != null)
            HardwareBoost.ChangedExternally += OnHardwareBoostChangedExternally;
        else
            HardwareBoostUnavailableReason = reason;
    }

    /// <summary>Starts/stops the virtual-cable router according to settings.</summary>
    private void UpdateRouter()
    {
        if (!_settings.UseVirtualCable || _device == null)
        {
            Router.Stop();
            RouterError = null;
            return;
        }

        var cables = VirtualCableRouter.FindCables(_devices.Enumerator);
        var cableId = _settings.VirtualCableDeviceId;
        if (cableId == null || !cables.Any(c => string.Equals(c.Id, cableId, StringComparison.OrdinalIgnoreCase)))
            cableId = cables.FirstOrDefault().Id;

        if (cableId == null)
        {
            Router.Stop();
            RouterError = "No virtual audio cable found. Install VB-CABLE and try again.";
            return;
        }

        if (Router.IsRunning && string.Equals(Router.SourceDeviceId, _device.ID, StringComparison.OrdinalIgnoreCase))
            return;

        var cable = _devices.TryGetDevice(cableId);
        if (cable == null)
        {
            RouterError = "The virtual cable device is not available.";
            return;
        }
        try
        {
            Router.Start(_device, cable);
            RouterError = null;
        }
        catch (Exception ex)
        {
            RouterError = $"Could not start routing: {ex.Message}";
            Log.Warn(RouterError);
        }
    }

    /// <summary>Re-detects all boost methods after a settings change and re-applies the level.</summary>
    public void RefreshBoostMethods()
    {
        if (_device == null) return;
        try
        {
            if (!_settings.UseHardwareBoost && HardwareBoost != null)
                HardwareBoost.Set(HardwareBoost.NeutralDb); // leave the driver at "no boost"
        }
        catch (COMException) { }

        _apo.Refresh();
        OpenHardwareBoost();
        if (!_settings.UseVirtualCable) Router.Stop();
        UpdateRouter();
        _lastApoContent = null;
        Apply();
    }

    // ---------------------------------------------------------------------------------
    // Level / mute
    // ---------------------------------------------------------------------------------

    public void SetLevel(double percent)
    {
        if (_device == null) return;
        _level = Math.Round(Math.Clamp(percent, 0, 200), 1);
        Apply();
    }

    public void Adjust(double deltaPercent) => SetLevel(Math.Round(_level + deltaPercent));

    public void SetMuted(bool muted)
    {
        if (_endpoint == null) return;
        try
        {
            _endpoint.Mute = muted;
            _muted = muted;
        }
        catch (COMException ex)
        {
            HandleDeviceError(ex);
            return;
        }
        Publish(BuildState(State.DesiredBoostDb, State.HardwareBoostDb, State.SoftwareBoostDb, State.SoftwareMethod, State.BoostLimited));
    }

    public void ToggleMute() => SetMuted(!_muted);

    /// <summary>Pushes the current level (and boost split) to the device.</summary>
    public void Apply()
    {
        if (_device == null || _endpoint == null) return;

        try
        {
            // --- 1. Endpoint master volume --------------------------------------------
            float scalar = BoostPlanner.EndpointScalar(_level);
            if (MathF.Abs(_endpoint.MasterVolumeLevelScalar - scalar) > 0.0005f)
                _endpoint.MasterVolumeLevelScalar = scalar;

            // --- 2. Split the requested boost across the available methods ----------------
            float desired = BoostPlanner.DesiredBoostDb(_level, _settings.MaxBoostDb);
            var (softMethod, problem) = ResolveSoftwareMethod();
            var plan = BoostPlanner.Plan(desired, HardwareBoost?.Range, softMethod != SoftwareBoostMethod.None);

            float hwDb = 0f;
            if (HardwareBoost is { } hw)
                hwDb = hw.Set(plan.HardwareLevelDb) - hw.NeutralDb;

            float softDb = plan.SoftwareDb;
            Router.GainDb = softMethod == SoftwareBoostMethod.VirtualCable ? softDb : 0f;
            bool limited = plan.Limited;

            // --- 3. Remember per device --------------------------------------------------
            var ds = _settings.GetDevice(_device.ID);
            ds.LevelPercent = _level;
            ds.HardwareBoostDb = Math.Round(hwDb, 2);
            ds.SoftwareBoostDb = Math.Round(softDb, 2);
            ds.SoftwareBoostMethod = softMethod == SoftwareBoostMethod.None ? null : softMethod.ToString();
            SettingsDirty?.Invoke(this, EventArgs.Empty);

            if (_apo.IsInstalled)
            {
                _apoWriteTimer.Stop();
                _apoWriteTimer.Start();
            }

            Publish(BuildState(desired, hwDb, softDb, softMethod, limited));

            if (limited && problem.HasValue)
                SoftwareBoostUnavailable?.Invoke(this, problem.Value);
        }
        catch (COMException ex)
        {
            HandleDeviceError(ex);
        }
    }

    /// <summary>Picks the software stage in priority order: Equalizer APO, then the virtual cable.</summary>
    private (SoftwareBoostMethod method, SoftwareBoostProblem? problem) ResolveSoftwareMethod()
    {
        SoftwareBoostProblem? problem = null;
        if (_settings.UseEqualizerApo && _device != null)
        {
            switch (_apo.GetStatus(_device.ID))
            {
                case ApoStatus.Ready: return (SoftwareBoostMethod.EqualizerApo, null);
                case ApoStatus.NotInstalled: problem = SoftwareBoostProblem.ApoNotInstalled; break;
                case ApoStatus.NotEnabledForDevice: problem = SoftwareBoostProblem.ApoNotEnabledForDevice; break;
                case ApoStatus.SetupRequired: problem = SoftwareBoostProblem.ApoSetupRequired; break;
            }
        }

        if (_settings.UseVirtualCable && Router.IsRunning)
            return (SoftwareBoostMethod.VirtualCable, null);

        return (SoftwareBoostMethod.None, problem);
    }

    /// <summary>Estimates the slider position from the device's current state (first use / external change).</summary>
    private double DeriveLevelFromDevice()
    {
        if (_endpoint == null) return 100;
        float scalar = _endpoint.MasterVolumeLevelScalar;
        if (scalar < 0.995f) return Math.Round(scalar * 100.0, 1);

        double boost = 0;
        if (HardwareBoost is { } hw) boost += hw.CurrentDb - hw.NeutralDb;
        if (_device != null && _settings.Devices.TryGetValue(_device.ID, out var ds)) boost += ds.SoftwareBoostDb;
        return Math.Round(100 + Math.Clamp(boost / _settings.MaxBoostDb * 100.0, 0, 100), 1);
    }

    private void FlushApo()
    {
        if (!_apo.IsInstalled) return;

        // One block per device that currently has an APO boost, so every mic keeps its own
        // preamp even while another one is selected in MicBoost.
        // Turning APO off in settings clears every block (an empty file = no preamp anywhere).
        var blocks = new Dictionary<string, (string Name, double Db)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, ds) in _settings.Devices)
        {
            if (_settings.UseEqualizerApo &&
                ds.SoftwareBoostMethod == nameof(SoftwareBoostMethod.EqualizerApo) && ds.SoftwareBoostDb > 0.001)
                blocks[id] = (ds.Name ?? "Microphone", ds.SoftwareBoostDb);
        }

        var signature = string.Join("|", blocks.OrderBy(b => b.Key).Select(b => $"{b.Key}={b.Value.Db:0.00}"));
        if (signature == _lastApoContent) return;
        _lastApoContent = signature;

        if (!_apo.WritePreamps(blocks) && blocks.Count > 0)
        {
            // Permissions were lost (e.g. APO reinstalled): the next Apply reports SetupRequired.
            Log.Warn("Equalizer APO include file is not writable.");
            _lastApoContent = null;
            Apply();
        }
    }

    // ---------------------------------------------------------------------------------
    // Change notifications (Lock Volume)
    // ---------------------------------------------------------------------------------

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        // Core Audio worker thread. Our own changes carry EventContext and are ignored.
        if (data.EventContext == EventContext) return;
        float scalar = data.MasterVolume;
        bool muted = data.Muted;
        _ui.Post(_ => HandleExternalChange(scalar, muted), null);
    }

    private void HandleExternalChange(float scalar, bool muted)
    {
        if (_device == null) return;

        bool changed = muted != _muted;
        _muted = muted;

        float expected = (float)Math.Min(_level, 100) / 100f;
        if (MathF.Abs(scalar - expected) > LevelTolerance)
        {
            if (_settings.LockVolume)
            {
                _restoreTimer.Stop();
                _restoreTimer.Start();
            }
            else
            {
                // Follow the change (e.g. the user moved the Windows slider).
                _level = Math.Round(scalar * 100.0, 1);
                Apply();
                return;
            }
        }

        if (changed)
            Publish(BuildState(State.DesiredBoostDb, State.HardwareBoostDb, State.SoftwareBoostDb, State.SoftwareMethod, State.BoostLimited));
    }

    private void OnHardwareBoostChangedExternally(object? sender, EventArgs e) => _ui.Post(_ =>
    {
        if (_device == null || HardwareBoost == null) return;
        if (_settings.LockVolume)
        {
            _restoreTimer.Stop();
            _restoreTimer.Start();
        }
        else
        {
            _level = DeriveLevelFromDevice();
            Apply();
        }
    }, null);

    // ---------------------------------------------------------------------------------
    // Level meter
    // ---------------------------------------------------------------------------------

    /// <summary>Requests metering (only while the popup is visible); the stream opens lazily.</summary>
    public void StartMetering()
    {
        _meteringRequested = true;
        _monitorFailed = false;
    }

    public void StopMetering()
    {
        _meteringRequested = false;
        _monitor.Stop();
    }

    /// <summary>
    /// Peak since the last call. Uses the router's post-gain signal when it runs; otherwise
    /// (re)opens the metering capture on demand, so the meter survives device switches.
    /// </summary>
    public float ReadPeak()
    {
        if (Router.IsRunning)
        {
            if (_monitor.IsRunning) _monitor.Stop();
            return Router.ReadPeak();
        }
        if (_meteringRequested && !_monitor.IsRunning && !_monitorFailed && _device != null)
            _monitorFailed = !_monitor.Start(_device); // don't retry 30x/s if capture is blocked
        return _monitor.ReadAndResetPeak();
    }

    // ---------------------------------------------------------------------------------

    private VolumeState BuildState(float desired, float hwDb, float softDb, SoftwareBoostMethod method, bool limited)
    {
        if (_device == null || _endpoint == null) return VolumeState.NoDevice;
        float endpointDb = 0f;
        try { endpointDb = _endpoint.MasterVolumeLevel; } catch (COMException) { }
        return new VolumeState(true, _device.ID, _settings.GetDevice(_device.ID).Name ?? "Microphone",
            _level, _muted, endpointDb, desired, hwDb, softDb, method, limited);
    }

    private void Publish(VolumeState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void HandleDeviceError(COMException ex)
    {
        Log.Warn($"Device error (0x{ex.HResult:X8}): {ex.Message}");
        Detach();
        Publish(VolumeState.NoDevice);
        DeviceLost?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _apoWriteTimer.Stop();
        FlushApo();
        _apoWriteTimer.Dispose();
        _restoreTimer.Dispose();
        Detach();
        Router.Dispose();
        _monitor.Dispose();
    }
}
