using System.Globalization;
using MicBoost.Audio;
using MicBoost.Core;

namespace MicBoost.UI;

/// <summary>
/// Small flyout shown next to the tray icon: level slider (0..200 %), % and dB readout,
/// mute button and a live input meter. It hides itself when it loses focus; closing it
/// never exits the app. The mouse wheel anywhere over it changes the level in 2 % steps.
/// </summary>
public sealed class PopupForm : Form
{
    private const int WheelStepPercent = 2;

    private readonly VolumeController _controller;
    private readonly Label _deviceLabel = new() { AutoEllipsis = true, AutoSize = false };
    private readonly Label _percentLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label _dbLabel = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
    private readonly VolumeSlider _slider = new();
    private readonly LevelMeter _meter = new();
    private readonly Button _muteButton = new() { TabStop = true };
    private readonly Label _detailLabel = new() { AutoEllipsis = true, AutoSize = false, TextAlign = ContentAlignment.MiddleRight };
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 33 };
    private ThemePalette _palette = ThemeManager.Current;
    private int _wheelRemainder;
    private int _layoutDpi;

    /// <summary>Time the popup was last hidden by losing focus (to debounce tray clicks).</summary>
    public DateTime LastDeactivated { get; private set; }

    public event EventHandler? SettingsRequested;

    public PopupForm(VolumeController controller)
    {
        _controller = controller;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None; // layout is computed from DeviceDpi below
        Text = "MicBoost";
        _muteButton.Click += (_, _) => _controller.ToggleMute();
        _deviceLabel.Cursor = Cursors.Hand;
        _deviceLabel.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _slider.ValueChangedByUser += (_, _) => _controller.SetLevel(_slider.Value);
        _meterTimer.Tick += (_, _) => _meter.Push(_controller.ReadPeak());

        Controls.AddRange(new Control[] { _deviceLabel, _percentLabel, _dbLabel, _slider, _meter, _muteButton, _detailLabel });

        // Route wheel events from every child to one handler.
        MouseWheel += OnAnyMouseWheel;
        foreach (Control c in Controls) c.MouseWheel += OnAnyMouseWheel;

        _controller.StateChanged += (_, state) => UpdateFromState(state);
        ApplyTheme(ThemeManager.Current);
        UpdateFromState(_controller.State);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            const int CS_DROPSHADOW = 0x20000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW; // keep out of Alt+Tab
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyRoundedCorners(this);
    }

    private int S(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    /// <summary>Lays out the fixed-size flyout (and pixel-sized fonts) for the current DPI.</summary>
    private void DoLayoutForDpi()
    {
        if (_layoutDpi != DeviceDpi)
        {
            _layoutDpi = DeviceDpi;
            SetFont(this, "Segoe UI", 12);
            SetFont(_deviceLabel, "Segoe UI", 12);
            SetFont(_muteButton, "Segoe UI", 12);
            SetFont(_percentLabel, "Segoe UI Semibold", 27);
            SetFont(_dbLabel, "Segoe UI", 15);
            SetFont(_detailLabel, "Segoe UI", 11);
        }

        int w = S(320), pad = S(14);
        ClientSize = new Size(w, S(170));
        int inner = w - 2 * pad;

        _deviceLabel.SetBounds(pad, S(10), inner, S(20));
        _percentLabel.SetBounds(pad, S(30), inner / 2, S(42));
        _dbLabel.SetBounds(pad + inner / 2, S(30), inner / 2, S(42));
        _slider.SetBounds(pad - S(2), S(76), inner + S(4), S(26));
        _meter.SetBounds(pad, S(108), inner, S(8));
        _muteButton.SetBounds(pad, S(128), S(84), S(28));
        _detailLabel.SetBounds(pad + S(90), S(128), inner - S(90), S(28));
    }

    private void SetFont(Control c, string family, float px)
    {
        var old = c.Font;
        c.Font = new Font(family, S(px), GraphicsUnit.Pixel);
        if (!ReferenceEquals(old, Control.DefaultFont) && !ReferenceEquals(old, Font)) old.Dispose();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        DoLayoutForDpi();
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.Background;
        ForeColor = palette.Text;
        _deviceLabel.ForeColor = palette.SubtleText;
        _dbLabel.ForeColor = palette.SubtleText;
        _detailLabel.ForeColor = palette.SubtleText;
        _muteButton.FlatStyle = FlatStyle.Flat;
        _muteButton.FlatAppearance.BorderColor = palette.Border;
        _muteButton.BackColor = palette.Surface;
        _muteButton.ForeColor = palette.Text;
        _slider.ApplyTheme(palette);
        _meter.ApplyTheme(palette);
        UpdateFromState(_controller.State);
        Invalidate(true);
    }

    private void UpdateFromState(VolumeState state)
    {
        if (IsDisposed) return;

        _deviceLabel.Text = state.DeviceName;
        _slider.Enabled = state.HasDevice;
        _muteButton.Enabled = state.HasDevice;

        if (!state.HasDevice)
        {
            _percentLabel.Text = "--";
            _dbLabel.Text = string.Empty;
            _detailLabel.Text = "Plug in a microphone";
            _muteButton.Text = "Mute";
            return;
        }

        _slider.Value = state.LevelPercent;
        _percentLabel.Text = $"{state.LevelPercent:0}%";
        _percentLabel.ForeColor = state.Muted ? _palette.SubtleText : state.IsBoosted ? _palette.Boost : _palette.Text;
        _dbLabel.Text = state.Muted ? "Muted" : FormatDb(state.TotalDb);
        _muteButton.Text = state.Muted ? "Unmute" : "Mute";
        _muteButton.ForeColor = state.Muted ? _palette.Danger : _palette.Text;
        _detailLabel.Text = DescribeBoost(state);
    }

    /// <summary>"Boost +23.0 dB (HW +20 · APO +3.0)" - always the exact applied values.</summary>
    public static string DescribeBoost(VolumeState s)
    {
        if (!s.IsBoosted) return $"Endpoint {FormatDb(s.EndpointDb)}";

        var parts = new List<string>();
        if (s.HardwareBoostDb > 0.01f) parts.Add($"HW {FormatDb(s.HardwareBoostDb, 0)}");
        if (s.SoftwareBoostDb > 0.01f)
            parts.Add($"{(s.SoftwareMethod == SoftwareBoostMethod.EqualizerApo ? "APO" : "Cable")} {FormatDb(s.SoftwareBoostDb)}");

        var text = $"Boost {FormatDb(s.TotalBoostDb)}";
        if (parts.Count > 0) text += $" ({string.Join(" · ", parts)})";
        if (s.BoostLimited) text += " - max";
        return text;
    }

    public static string FormatDb(float db, int decimals = 1)
    {
        var format = decimals == 0 ? "+0;-0;0" : "+0.0;-0.0;0.0";
        return db.ToString(format, CultureInfo.InvariantCulture).Replace('-', '−') + " dB";
    }

    private void OnAnyMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        // Accumulate for high-resolution wheels/touchpads: one notch (120) = one 2 % step.
        _wheelRemainder += e.Delta;
        int notches = _wheelRemainder / SystemInformation.MouseWheelScrollDelta;
        if (notches == 0) return;
        _wheelRemainder -= notches * SystemInformation.MouseWheelScrollDelta;
        _controller.Adjust(notches * WheelStepPercent);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Hide();
                e.Handled = true;
                break;
            case Keys.M:
                _controller.ToggleMute();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Shows the flyout anchored to the taskbar corner nearest the cursor.</summary>
    public void ShowNearTray()
    {
        if (!IsHandleCreated) CreateControl();
        var anchor = Cursor.Position;
        DoLayoutForDpi();
        PositionNear(anchor);
        Show();
        // Moving to a monitor with another DPI resizes the window; re-layout and re-anchor.
        DoLayoutForDpi();
        PositionNear(anchor);
        Activate();
        _slider.Focus();
    }

    private void PositionNear(Point cursor)
    {
        var screen = Screen.FromPoint(cursor);
        var wa = screen.WorkingArea;
        var b = screen.Bounds;
        int margin = S(12);
        int x, y;

        if (wa.Top > b.Top) // taskbar at the top
        {
            x = cursor.X - Width / 2;
            y = wa.Top + margin;
        }
        else if (wa.Left > b.Left) // taskbar on the left
        {
            x = wa.Left + margin;
            y = cursor.Y - Height / 2;
        }
        else if (wa.Right < b.Right) // taskbar on the right
        {
            x = wa.Right - Width - margin;
            y = cursor.Y - Height / 2;
        }
        else // bottom (default, also auto-hide)
        {
            x = wa.Right - Width - margin;
            y = wa.Bottom - Height - margin;
        }

        x = Math.Clamp(x, wa.Left + margin, wa.Right - Width - margin);
        y = Math.Clamp(y, wa.Top + margin, wa.Bottom - Height - margin);
        Location = new Point(x, y);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            _meter.Reset();
            _controller.StartMetering();
            _meterTimer.Start();
        }
        else
        {
            // No metering, no timers while hidden: keeps idle CPU at ~0 %.
            _meterTimer.Stop();
            _controller.StopMetering();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        LastDeactivated = DateTime.UtcNow;
        Hide();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 or similar just hides the flyout; only "Exit" in the tray menu quits.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Thin border so the flyout stands out on Windows 10 (no rounded DWM corners there).
        using var pen = new Pen(_palette.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _meterTimer.Dispose();
        base.Dispose(disposing);
    }
}
