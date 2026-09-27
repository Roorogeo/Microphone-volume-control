using System.Diagnostics;
using MicBoost.Audio;
using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>
/// Settings window. Edits a working copy of the values and only writes them back to
/// <see cref="AppSettings"/> when the user clicks OK.
/// </summary>
public sealed class SettingsForm : Form
{
    public const string VbCableUrl = "https://vb-audio.com/Cable/";

    private readonly Func<string> _statusProvider;
    private readonly Func<Task<bool>> _apoSetup;

    private readonly CheckBox _startup = Check("Start with Windows");
    private readonly CheckBox _lock = Check("Lock volume (undo level changes made by other apps or auto-gain)");
    private readonly CheckBox _followDefault = Check("Always use the Windows default microphone");
    private readonly CheckBox _osd = Check("Show an on-screen display when a hotkey changes the level");

    private readonly NumericUpDown _maxBoost = new() { Minimum = 3, Maximum = 40, DecimalPlaces = 1, Increment = 1, Width = 70 };
    private readonly CheckBox _useHw = Check("Use the hardware Microphone Boost (driver)");
    private readonly CheckBox _useApo = Check("Use Equalizer APO for software gain");
    private readonly CheckBox _useCable = Check("Virtual cable fallback - send the boosted mic to:");
    private readonly ComboBox _cable = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _apoSetupButton = new() { Text = "Set up Equalizer APO…", AutoSize = true };

    private readonly CheckBox _hotkeysEnabled = Check("Enable global hotkeys");
    private readonly HotkeyTextBox _hkUp = new() { Width = 180 };
    private readonly HotkeyTextBox _hkDown = new() { Width = 180 };
    private readonly HotkeyTextBox _hkMute = new() { Width = 180 };

    private readonly List<(string? Id, string Name)> _cableItems;
    private readonly IntPtr _iconHandle;

    public SettingsForm(AppSettings settings, List<(string Id, string Name)> cables,
                        Func<string> statusProvider, Func<Task<bool>> apoSetup)
    {
        _statusProvider = statusProvider;
        _apoSetup = apoSetup;

        Text = "MicBoost Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true; // the only MicBoost window that should be findable in the taskbar
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(12);

        using (var bmp = TrayIcons.Draw(32, TrayIconKind.Normal, ThemeManager.Current.IsDark ? Color.White : Color.FromArgb(28, 28, 28)))
        {
            _iconHandle = bmp.GetHicon();
            Icon = Icon.FromHandle(_iconHandle);
        }

        // Cable choices: "Auto-detect" plus every render device that looks like a virtual cable.
        _cableItems = new List<(string?, string)> { (null, "Auto-detect") };
        _cableItems.AddRange(cables.Select(c => ((string?)c.Id, c.Name)));
        foreach (var item in _cableItems) _cable.Items.Add(item.Name);
        int selected = _cableItems.FindIndex(c => string.Equals(c.Id, settings.VirtualCableDeviceId, StringComparison.OrdinalIgnoreCase));
        _cable.SelectedIndex = Math.Max(0, selected);

        // Current values
        _startup.Checked = StartupManager.IsEnabled;
        _lock.Checked = settings.LockVolume;
        _followDefault.Checked = settings.FollowDefaultDevice;
        _osd.Checked = settings.ShowOsd;
        _maxBoost.Value = (decimal)Math.Clamp(settings.MaxBoostDb, 3, 40);
        _useHw.Checked = settings.UseHardwareBoost;
        _useApo.Checked = settings.UseEqualizerApo;
        _useCable.Checked = settings.UseVirtualCable;
        _hotkeysEnabled.Checked = settings.HotkeysEnabled;
        _hkUp.Hotkey = HotkeyFormat.Parse(settings.HotkeyVolumeUp);
        _hkDown.Hotkey = HotkeyFormat.Parse(settings.HotkeyVolumeDown);
        _hkMute.Hotkey = HotkeyFormat.Parse(settings.HotkeyToggleMute);

        BuildLayout();

        _useCable.CheckedChanged += (_, _) => _cable.Enabled = _useCable.Checked;
        _cable.Enabled = _useCable.Checked;
        _hotkeysEnabled.CheckedChanged += (_, _) => UpdateHotkeyEnabled();
        UpdateHotkeyEnabled();
        _apoSetupButton.Click += async (_, _) =>
        {
            _apoSetupButton.Enabled = false;
            await _apoSetup();
            _apoSetupButton.Enabled = true;
            RefreshStatus();
        };
        RefreshStatus();
        ApplyTheme(ThemeManager.Current);
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 3, 3, 3) };

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };

        // --- General ----------------------------------------------------------------
        root.Controls.Add(Section("General", _startup, _lock, _followDefault, _osd));

        // --- Boost ------------------------------------------------------------------
        var maxRow = Row(new Label { Text = "Total boost at 200 %:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) },
                         _maxBoost,
                         new Label { Text = "dB   (100–200 % maps linearly onto 0 dB … this value)", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
        var cableRow = Row(_useCable, _cable);
        var links = Row(_apoSetupButton,
                        Link("Download Equalizer APO", EqualizerApo.DownloadUrl),
                        Link("Download VB-CABLE", VbCableUrl));
        root.Controls.Add(Section("Boost above 100 %", maxRow, _useHw, _useApo, cableRow, _status, links));

        // --- Hotkeys ------------------------------------------------------------------
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(20, 0, 3, 3) };
        grid.Controls.Add(HotkeyLabel("Volume up (+5 %)"), 0, 0);
        grid.Controls.Add(_hkUp, 1, 0);
        grid.Controls.Add(HotkeyLabel("Volume down (−5 %)"), 0, 1);
        grid.Controls.Add(_hkDown, 1, 1);
        grid.Controls.Add(HotkeyLabel("Toggle mute"), 0, 2);
        grid.Controls.Add(_hkMute, 1, 2);
        var hint = new Label
        {
            Text = "Click a box and press the new combination. Backspace clears it.",
            AutoSize = true,
            Margin = new Padding(20, 0, 3, 3),
        };
        root.Controls.Add(Section("Hotkeys", _hotkeysEnabled, grid, hint));

        // --- Buttons ------------------------------------------------------------------
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 0) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 0) };
        var folder = Link("Open settings folder", AppPaths.DataDirectory);
        var buttons = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folder.Anchor = AnchorStyles.Left;
        buttons.Controls.Add(folder, 0, 0);
        buttons.Controls.Add(ok, 1, 0);
        buttons.Controls.Add(cancel, 2, 0);
        root.Controls.Add(buttons);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(root);
    }

    private static GroupBox Section(string title, params Control[] children)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
        };
        flow.Controls.AddRange(children);
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 8),
            Margin = new Padding(0, 0, 0, 8),
        };
        box.Controls.Add(flow);
        return box;
    }

    private static FlowLayoutPanel Row(params Control[] children)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(children);
        return row;
    }

    private static Label HotkeyLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };

    private static LinkLabel Link(string text, string target)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(8, 8, 3, 3) };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn($"Could not open {target}: {ex.Message}"); }
        };
        return link;
    }

    private void UpdateHotkeyEnabled()
    {
        _hkUp.Enabled = _hkDown.Enabled = _hkMute.Enabled = _hotkeysEnabled.Checked;
    }

    private void RefreshStatus() => _status.Text = _statusProvider();

    public void ApplyTheme(ThemePalette palette)
    {
        ThemeManager.ApplyToControls(this, palette);
        _status.ForeColor = palette.SubtleText;
        if (IsHandleCreated) ThemeManager.ApplyTitleBar(this, palette.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyTitleBar(this, ThemeManager.Current.IsDark);
    }

    /// <summary>Whether "Start with Windows" was ticked (applied by the caller).</summary>
    public bool StartWithWindows => _startup.Checked;

    /// <summary>Copies the edited values into <paramref name="settings"/>.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.LockVolume = _lock.Checked;
        settings.FollowDefaultDevice = _followDefault.Checked;
        settings.ShowOsd = _osd.Checked;
        settings.MaxBoostDb = (double)_maxBoost.Value;
        settings.UseHardwareBoost = _useHw.Checked;
        settings.UseEqualizerApo = _useApo.Checked;
        settings.UseVirtualCable = _useCable.Checked;
        settings.VirtualCableDeviceId = _cableItems[Math.Max(0, _cable.SelectedIndex)].Id;
        settings.HotkeysEnabled = _hotkeysEnabled.Checked;
        settings.HotkeyVolumeUp = HotkeyFormat.Format(_hkUp.Hotkey);
        settings.HotkeyVolumeDown = HotkeyFormat.Format(_hkDown.Hotkey);
        settings.HotkeyToggleMute = HotkeyFormat.Format(_hkMute.Hotkey);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_iconHandle != IntPtr.Zero) TrayIcons.DestroyIcon(_iconHandle);
    }
}
