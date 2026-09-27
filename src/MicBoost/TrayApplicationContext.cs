using Microsoft.Win32;
using MicBoost.Audio;
using MicBoost.Core;
using MicBoost.UI;

namespace MicBoost;

/// <summary>
/// The application: tray icon, context menu, popup, hotkeys and the glue between them and
/// <see cref="VolumeController"/>. There is no main window; the app lives until "Exit".
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const double HotkeyStepPercent = 5;

    private readonly SynchronizationContext _ui;
    private readonly AppSettings _settings;
    private readonly AudioDeviceManager _devices;
    private readonly EqualizerApo _apo;
    private readonly VolumeController _controller;
    private readonly TrayIcons _icons;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly PopupForm _popup;
    private readonly OsdForm _osd;
    private readonly HotkeyManager _hotkeys;
    private readonly System.Windows.Forms.Timer _saveTimer;
    private readonly RegisteredWaitHandle _showWait;

    private SettingsForm? _settingsForm;
    private Font? _boldMenuFont;
    private TrayIconKind _iconKind = (TrayIconKind)(-1);
    private bool _dialogOpen;
    private bool _apoSetupAskedThisSession;
    private (string? Id, int Count, DateTime Since) _failures;
    private bool _disposed;

    public TrayApplicationContext(EventWaitHandle showEvent)
    {
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;

        _settings = SettingsStore.Load();
        StartupManager.RefreshPathIfEnabled();

        _saveTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SettingsStore.Save(_settings);
        };

        // --- Audio ------------------------------------------------------------------------
        _devices = new AudioDeviceManager(_ui);
        _devices.DevicesChanged += (_, _) => SelectBestDevice();
        _apo = new EqualizerApo();
        _controller = new VolumeController(_settings, _devices, _apo, _ui);
        _controller.StateChanged += (_, state) => UpdateTray(state);
        _controller.SettingsDirty += (_, _) => ScheduleSave();
        _controller.DeviceLost += (_, _) => OnDeviceLost();
        _controller.SoftwareBoostUnavailable += (_, problem) => OnSoftwareBoostUnavailable(problem);

        // --- UI ---------------------------------------------------------------------------
        _icons = new TrayIcons();
        _popup = new PopupForm(_controller);
        _popup.SettingsRequested += (_, _) => ShowSettings();
        _ = _popup.Handle; // create the window now so it is ready on first click
        _osd = new OsdForm();

        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false; // WinForms cancels opening when the menu was empty beforehand
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Get(TrayIconKind.NoDevice),
            Text = "MicBoost",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePopup();
        };

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += (_, action) => OnHotkey(action);
        RegisterHotkeys();

        ThemeManager.ThemeChanged += OnThemeChanged;
        ApplyMenuTheme();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionEnded += OnSessionEnded;

        // A second MicBoost launch signals this event: open the popup.
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => _ui.Post(_ => ShowPopup(), null), null, Timeout.Infinite, executeOnlyOnce: false);

        SelectBestDevice();
    }

    // -------------------------------------------------------------------------------------
    // Devices
    // -------------------------------------------------------------------------------------

    /// <summary>Picks the user's chosen mic if present, otherwise the Windows default.</summary>
    private void SelectBestDevice()
    {
        var list = _devices.GetCaptureDevices();
        string? target = null;

        if (!_settings.FollowDefaultDevice && _settings.SelectedDeviceId != null &&
            list.Any(d => string.Equals(d.Id, _settings.SelectedDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            target = _settings.SelectedDeviceId;
        }
        target ??= list.FirstOrDefault(d => d.IsDefault)?.Id ?? list.FirstOrDefault()?.Id;

        // Don't hammer a device that keeps failing (e.g. a driver in a bad state).
        if (target != null && string.Equals(_failures.Id, target, StringComparison.OrdinalIgnoreCase) &&
            _failures.Count >= 3 && DateTime.UtcNow - _failures.Since < TimeSpan.FromSeconds(10))
        {
            target = list.FirstOrDefault(d => !string.Equals(d.Id, _failures.Id, StringComparison.OrdinalIgnoreCase))?.Id;
        }

        _controller.SelectDevice(target);
    }

    private void OnDeviceLost()
    {
        var id = _controller.State.DeviceId ?? _failures.Id;
        if (string.Equals(_failures.Id, id, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _failures.Since < TimeSpan.FromSeconds(10))
            _failures = (id, _failures.Count + 1, _failures.Since);
        else
            _failures = (id, 1, DateTime.UtcNow);

        // Re-evaluate shortly; the unplug notification usually arrives right after.
        var retry = new System.Windows.Forms.Timer { Interval = 500 };
        retry.Tick += (_, _) =>
        {
            retry.Dispose();
            SelectBestDevice();
        };
        retry.Start();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Some drivers reset the mic level/boost after sleep; re-apply the user's level.
        if (e.Mode == PowerModes.Resume)
            _ui.Post(_ => { SelectBestDevice(); _controller.Apply(); }, null);
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => SettingsStore.Save(_settings);

    // -------------------------------------------------------------------------------------
    // Tray icon, tooltip, popup
    // -------------------------------------------------------------------------------------

    private void UpdateTray(VolumeState state)
    {
        var kind = !state.HasDevice ? TrayIconKind.NoDevice
            : state.Muted ? TrayIconKind.Muted
            : state.IsBoosted ? TrayIconKind.Boosted
            : TrayIconKind.Normal;
        if (kind != _iconKind)
        {
            _iconKind = kind;
            _notifyIcon.Icon = _icons.Get(kind);
        }

        string text;
        if (!state.HasDevice)
        {
            text = "MicBoost - no microphone found";
        }
        else
        {
            var level = state.Muted ? "Muted" : $"{state.LevelPercent:0}%  ({PopupForm.FormatDb(state.TotalDb)})";
            if (state.Muted && state.LevelPercent > 0) level += $" at {state.LevelPercent:0}%";
            var name = state.DeviceName;
            const int limit = 127; // NotifyIcon.Text maximum
            int room = limit - level.Length - 1;
            if (name.Length > room) name = name[..Math.Max(0, room - 1)] + "…";
            text = $"{name}\n{level}";
        }
        if (_notifyIcon.Text != text) _notifyIcon.Text = text;
    }

    private void TogglePopup()
    {
        if (_popup.Visible)
        {
            _popup.Hide();
            return;
        }
        // Clicking the tray icon while the popup is open first deactivates (hides) it;
        // don't immediately re-open it on that same click.
        if (DateTime.UtcNow - _popup.LastDeactivated < TimeSpan.FromMilliseconds(300)) return;
        ShowPopup();
    }

    private void ShowPopup()
    {
        if (_dialogOpen) return;
        _osd.Hide();
        _popup.ShowNearTray();
    }

    // -------------------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------------------

    private void BuildMenu()
    {
        foreach (var old in _menu.Items.Cast<ToolStripItem>().ToList()) old.Dispose();
        _menu.Items.Clear();
        var state = _controller.State;

        _boldMenuFont ??= new Font(_menu.Font, FontStyle.Bold);
        var header = new ToolStripMenuItem("Microphone") { Enabled = false, Font = _boldMenuFont };
        _menu.Items.Add(header);

        var devices = _devices.GetCaptureDevices();
        if (devices.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("No microphones found") { Enabled = false });

        foreach (var device in devices)
        {
            var item = new ToolStripMenuItem(device.IsDefault ? $"{device.Name}  (default)" : device.Name)
            {
                Checked = string.Equals(device.Id, state.DeviceId, StringComparison.OrdinalIgnoreCase),
            };
            var id = device.Id;
            item.Click += (_, _) =>
            {
                _settings.FollowDefaultDevice = false;
                _settings.SelectedDeviceId = id;
                ScheduleSave();
                SelectBestDevice();
            };
            _menu.Items.Add(item);
        }

        var followDefault = new ToolStripMenuItem("Always use Windows default") { Checked = _settings.FollowDefaultDevice };
        followDefault.Click += (_, _) =>
        {
            _settings.FollowDefaultDevice = !_settings.FollowDefaultDevice;
            if (!_settings.FollowDefaultDevice) _settings.SelectedDeviceId = state.DeviceId;
            ScheduleSave();
            SelectBestDevice();
        };
        _menu.Items.Add(followDefault);
        _menu.Items.Add(new ToolStripSeparator());

        var mute = new ToolStripMenuItem("Mute") { Checked = state.Muted, Enabled = state.HasDevice };
        mute.Click += (_, _) => _controller.ToggleMute();
        _menu.Items.Add(mute);

        var lockItem = new ToolStripMenuItem("Lock Volume") { Checked = _settings.LockVolume };
        lockItem.Click += (_, _) =>
        {
            _settings.LockVolume = !_settings.LockVolume;
            ScheduleSave();
            if (_settings.LockVolume) _controller.Apply(); // enforce immediately
        };
        _menu.Items.Add(lockItem);

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupManager.IsEnabled };
        startup.Click += (_, _) =>
        {
            try { StartupManager.SetEnabled(!StartupManager.IsEnabled); }
            catch (Exception ex) { Balloon($"Could not change autostart: {ex.Message}", ToolTipIcon.Warning); }
        };
        _menu.Items.Add(startup);
        _menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();
        _menu.Items.Add(settings);

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);
    }

    private void ApplyMenuTheme() => _menu.Renderer = new ThemedMenuRenderer(ThemeManager.Current);

    private void OnThemeChanged(object? sender, EventArgs e) => _ui.Post(_ =>
    {
        if (_disposed) return;
        var palette = ThemeManager.Current;
        _icons.Rebuild();
        _notifyIcon.Icon = _icons.Get(_iconKind < 0 ? TrayIconKind.NoDevice : _iconKind);
        _popup.ApplyTheme(palette);
        _osd.ApplyTheme(palette);
        _settingsForm?.ApplyTheme(palette);
        ApplyMenuTheme();
    }, null);

    // -------------------------------------------------------------------------------------
    // Hotkeys
    // -------------------------------------------------------------------------------------

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        if (!_settings.HotkeysEnabled) return;

        var failed = new List<string>();
        foreach (var (action, text) in new[]
                 {
                     (HotkeyAction.VolumeUp, _settings.HotkeyVolumeUp),
                     (HotkeyAction.VolumeDown, _settings.HotkeyVolumeDown),
                     (HotkeyAction.ToggleMute, _settings.HotkeyToggleMute),
                 })
        {
            var keys = HotkeyFormat.Parse(text);
            if (keys == Keys.None) continue;
            if (!_hotkeys.Register(action, keys)) failed.Add(HotkeyFormat.Format(keys));
        }

        if (failed.Count > 0)
            Balloon($"Hotkey {string.Join(", ", failed)} is already used by another app. Choose another in Settings.", ToolTipIcon.Warning);
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.VolumeUp: _controller.Adjust(+HotkeyStepPercent); break;
            case HotkeyAction.VolumeDown: _controller.Adjust(-HotkeyStepPercent); break;
            case HotkeyAction.ToggleMute: _controller.ToggleMute(); break;
        }
        if (_settings.ShowOsd && !_popup.Visible)
            _osd.ShowState(_controller.State);
    }

    // -------------------------------------------------------------------------------------
    // Settings and boost setup
    // -------------------------------------------------------------------------------------

    private void ShowSettings()
    {
        if (_settingsForm != null)
        {
            _settingsForm.Activate();
            return;
        }

        _popup.Hide();
        _hotkeys.UnregisterAll(); // so the hotkey boxes can record the current combinations
        bool apoBefore = _settings.UseEqualizerApo;
        try
        {
            using var form = new SettingsForm(_settings, VirtualCableRouter.FindCables(_devices.Enumerator),
                                              BuildStatusText, RunApoSetupAsync);
            _settingsForm = form;
            if (form.ShowDialog() == DialogResult.OK)
            {
                form.ApplyTo(_settings);
                if (_settings.UseEqualizerApo && !apoBefore) _apoSetupAskedThisSession = false;
                try { StartupManager.SetEnabled(form.StartWithWindows); }
                catch (Exception ex) { Balloon($"Could not change autostart: {ex.Message}", ToolTipIcon.Warning); }

                SettingsStore.Save(_settings);
                SelectBestDevice();
                _controller.RefreshBoostMethods();
            }
        }
        finally
        {
            _settingsForm = null;
            RegisterHotkeys();
        }
    }

    /// <summary>Human-readable summary of every boost method for the settings window.</summary>
    private string BuildStatusText()
    {
        var state = _controller.State;
        if (!state.HasDevice || state.DeviceId == null) return "No microphone connected.";

        _apo.Refresh();
        var hw = _controller.HardwareBoost?.ToString()
                 ?? $"not available - {_controller.HardwareBoostUnavailableReason}";
        var apo = _apo.GetStatus(state.DeviceId) switch
        {
            ApoStatus.NotInstalled => "not installed",
            ApoStatus.NotEnabledForDevice => "installed, but not enabled for this microphone (use its Configurator)",
            ApoStatus.SetupRequired => "enabled for this microphone - one-time setup needed (button below)",
            _ => $"ready ({Path.Combine(_apo.ConfigDirectory!, EqualizerApo.IncludeFileName)})",
        };
        var cable = !_settings.UseVirtualCable ? "off"
            : _controller.Router.IsRunning ? $"routing to \"{_controller.Router.CableName}\""
            : _controller.RouterError ?? "not running";

        return $"Microphone: {state.DeviceName}\n" +
               $"1. Hardware boost: {hw}\n" +
               $"2. Equalizer APO: {apo}\n" +
               $"3. Virtual cable: {cable}\n" +
               $"Now: {state.LevelPercent:0}% - {PopupForm.DescribeBoost(state)}";
    }

    private async Task<bool> RunApoSetupAsync()
    {
        _apo.Refresh();
        if (!_apo.IsInstalled)
        {
            Dialogs.ShowApoNotInstalled(_controller.HardwareBoost != null, _controller.HardwareBoost?.MaxContributionDb ?? 0);
            return false;
        }

        bool ok = await Task.Run(_apo.RunElevatedSetup);
        if (ok)
        {
            Balloon("Equalizer APO is connected. Boost above the hardware limit is now available.", ToolTipIcon.Info);
            _controller.RefreshBoostMethods();
        }
        else
        {
            Balloon("Equalizer APO setup was cancelled or failed. See the log in the settings folder.", ToolTipIcon.Warning);
        }
        return ok;
    }

    /// <summary>Shows each explanation at most once, deferred so it never interrupts a slider drag mid-event.</summary>
    private void OnSoftwareBoostUnavailable(SoftwareBoostProblem problem)
    {
        if (_dialogOpen) return;

        switch (problem)
        {
            case SoftwareBoostProblem.ApoNotInstalled when !_settings.ApoNoticeShown:
                _settings.ApoNoticeShown = true;
                ScheduleSave();
                ShowDialogDeferred(() => Dialogs.ShowApoNotInstalled(
                    _controller.HardwareBoost != null, _controller.HardwareBoost?.MaxContributionDb ?? 0));
                break;

            case SoftwareBoostProblem.ApoNotEnabledForDevice when !_settings.ApoNotEnabledNoticeShown:
                _settings.ApoNotEnabledNoticeShown = true;
                ScheduleSave();
                ShowDialogDeferred(() => Dialogs.ShowApoNotEnabled(_controller.State.DeviceName, _apo.ConfiguratorPath));
                break;

            case SoftwareBoostProblem.ApoSetupRequired when !_apoSetupAskedThisSession:
                _apoSetupAskedThisSession = true;
                ShowDialogDeferred(async () =>
                {
                    switch (Dialogs.AskApoSetup())
                    {
                        case ApoSetupAnswer.Continue:
                            await RunApoSetupAsync();
                            break;
                        case ApoSetupAnswer.Never:
                            _settings.UseEqualizerApo = false;
                            ScheduleSave();
                            _controller.RefreshBoostMethods();
                            break;
                    }
                });
                break;
        }
    }

    private void ShowDialogDeferred(Action show) => ShowDialogDeferred(() =>
    {
        show();
        return Task.CompletedTask;
    });

    private void ShowDialogDeferred(Func<Task> show)
    {
        _dialogOpen = true;
        _ui.Post(async _ =>
        {
            try
            {
                _popup.Hide();
                await show();
            }
            catch (Exception ex)
            {
                Log.Error($"Dialog failed: {ex}");
            }
            finally
            {
                _dialogOpen = false;
            }
        }, null);
    }

    // -------------------------------------------------------------------------------------

    private void Balloon(string text, ToolTipIcon icon) => _notifyIcon.ShowBalloonTip(5000, "MicBoost", text, icon);

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        SettingsStore.Save(_settings);
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionEnded -= OnSessionEnded;
            _showWait.Unregister(null);
            _saveTimer.Dispose();
            _hotkeys.Dispose();
            _controller.Dispose();
            _devices.Dispose();
            SettingsStore.Save(_settings);
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _boldMenuFont?.Dispose();
            _popup.Dispose();
            _osd.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
