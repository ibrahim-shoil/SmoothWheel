// SmoothWheel — smooth, trackpad-style scrolling for Windows mouse wheels.
// Copyright (C) 2026 ibrahim-shoil
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

namespace SmoothWheel;

/// <summary>
/// Owns the system-tray icon, its context menu, and the global toggle hotkey.
/// Bridges user actions to the <see cref="ScrollEngine"/> and <see cref="Config"/>.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private readonly ScrollEngine _engine = new();
    private readonly Config _config;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkey;
    private readonly ContextMenuStrip _menu;

    // Menu item references we tickle when state changes.
    private readonly ToolStripMenuItem _enableItem;
    private readonly ToolStripMenuItem _invertItem;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApp(Config config)
    {
        _config = config;

        _tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Visible = true,
            Text = "SmoothWheel"
        };

        _menu = new ContextMenuStrip();

        _enableItem = new ToolStripMenuItem("Enable smooth scrolling", null, OnToggleEnable);
        _menu.Items.Add(_enableItem);

        var duration = new ToolStripMenuItem("Glide duration");
        foreach (var ms in new[] { 60, 100, 150, 200, 300, 450, 600 })
        {
            duration.DropDownItems.Add(MakeRadioItem(ms + " ms", () => SetDuration(ms),
                () => _config.SmoothnessDurationMs == ms));
        }
        _menu.Items.Add(duration);

        var precision = new ToolStripMenuItem("Precision");
        foreach (var p in new[] { 2, 4, 8, 12, 16 })
        {
            precision.DropDownItems.Add(MakeRadioItem(p + " steps / notch", () => SetPrecision(p),
                () => _config.Precision == p));
        }
        _menu.Items.Add(precision);

        var speed = new ToolStripMenuItem("Speed");
        foreach (var s in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
        {
            double val = s;
            speed.DropDownItems.Add(MakeRadioItem(val.ToString("0.0") + "x", () => SetSpeed(val),
                () => Math.Abs(_config.SpeedFactor - val) < 0.001));
        }
        _menu.Items.Add(speed);

        var maxVel = new ToolStripMenuItem("Max momentum");
        foreach (var m in new[] { 2.0, 3.0, 4.0, 6.0, 8.0 })
        {
            double val = m;
            maxVel.DropDownItems.Add(MakeRadioItem(val.ToString("0.0") + "x", () => SetMaxVelocity(val),
                () => Math.Abs(_config.MaxVelocity - val) < 0.001));
        }
        _menu.Items.Add(maxVel);

        _invertItem = new ToolStripMenuItem("Invert direction", null, OnToggleInvert);
        _menu.Items.Add(_invertItem);

        _autostartItem = new ToolStripMenuItem("Start with Windows", null, OnToggleAutostart);
        _menu.Items.Add(_autostartItem);

        var debugItem = new ToolStripMenuItem("Debug logging (writes debug.log)", null, OnToggleDebug);
        _menu.Items.Add(debugItem);
        var openLogItem = new ToolStripMenuItem("Open debug.log folder", null, (_, _) => OpenLogFolder());
        _menu.Items.Add(openLogItem);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Exit());

        _tray.ContextMenuStrip = _menu;

        // Global hotkey window + registration. It also pumps the low-level hook.
        _hotkey = new HotkeyWindow(OnHotkey);
        _hotkey.Register(_config.HotkeyModifiers, (uint)_config.HotkeyVk);

        // Apply initial config + engine state.
        _engine.ApplySettings(_config);
        if (_config.Enabled) _engine.Start();
        UpdateChecks();
        UpdateTooltip();
    }

    // ---- Menu handlers -----------------------------------------------------

    private void OnToggleEnable(object? s, EventArgs e) => ToggleEnable();
    private void OnToggleInvert(object? s, EventArgs e)
    {
        _config.InvertDirection = !_config.InvertDirection;
        _engine.ApplySettings(_config);
        _config.Save();
        UpdateChecks();
    }
    private void OnToggleAutostart(object? s, EventArgs e)
    {
        _config.StartWithWindows = !_config.StartWithWindows;
        try
        {
            if (_config.StartWithWindows) Autostart.Enable();
            else Autostart.Disable();
        }
        catch { /* best-effort */ }
        _config.Save();
        UpdateChecks();
    }

    private void OnToggleDebug(object? s, EventArgs e)
    {
        if (DebugLog.Enabled) DebugLog.Stop();
        else DebugLog.Start();
        UpdateChecks();
    }

    private static void OpenLogFolder()
    {
        try
        {
            string dir = Path.GetDirectoryName(DebugLog.GetLogPath()) ?? "";
            if (Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch { /* best-effort */ }
    }

    private void OnHotkey() => ToggleEnable();

    private void ToggleEnable()
    {
        _config.Enabled = !_config.Enabled;
        if (_config.Enabled)
        {
            _engine.ApplySettings(_config);
            _engine.Start();
        }
        else
        {
            _engine.Stop();
        }
        _config.Save();
        UpdateChecks();
        UpdateTooltip();
    }

    private void SetDuration(int ms)
    {
        _config.SmoothnessDurationMs = ms;
        _engine.ApplySettings(_config);
        _config.Save();
        UpdateChecks();
    }

    private void SetPrecision(int p)
    {
        _config.Precision = p;
        _engine.ApplySettings(_config);
        _config.Save();
        UpdateChecks();
    }

    private void SetSpeed(double s)
    {
        _config.SpeedFactor = s;
        _engine.ApplySettings(_config);
        _config.Save();
        UpdateChecks();
    }

    private void SetMaxVelocity(double m)
    {
        _config.MaxVelocity = m;
        _engine.ApplySettings(_config);
        _config.Save();
        UpdateChecks();
    }

    // ---- Exit --------------------------------------------------------------

    public event EventHandler? ExitRequested;

    private void Exit()
    {
        _tray.Visible = false;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- Helpers -----------------------------------------------------------

    private void UpdateChecks()
    {
        _enableItem.Checked = _config.Enabled;
        _invertItem.Checked = _config.InvertDirection;
        // Start-with-Windows reflects registry truth, not just the config flag.
        try { _autostartItem.Checked = Autostart.IsEnabled(); }
        catch { _autostartItem.Checked = _config.StartWithWindows; }
        ((ToolStripMenuItem)_menu.Items[7]).Checked = DebugLog.Enabled;

        // Refresh radio-item checks for duration / precision / speed / maxVel submenus.
        foreach (ToolStripMenuItem sub in ((ToolStripMenuItem)_menu.Items[1]).DropDownItems)
            sub.Checked = _config.SmoothnessDurationMs == ParseLeadingInt(sub.Text);
        foreach (ToolStripMenuItem sub in ((ToolStripMenuItem)_menu.Items[2]).DropDownItems)
            sub.Checked = _config.Precision == ParseLeadingInt(sub.Text);
        foreach (ToolStripMenuItem sub in ((ToolStripMenuItem)_menu.Items[3]).DropDownItems)
            sub.Checked = Math.Abs(ParseLeadingDouble(sub.Text) - _config.SpeedFactor) < 0.001;
        foreach (ToolStripMenuItem sub in ((ToolStripMenuItem)_menu.Items[4]).DropDownItems)
            sub.Checked = Math.Abs(ParseLeadingDouble(sub.Text) - _config.MaxVelocity) < 0.001;
    }

    private void UpdateTooltip()
    {
        _tray.Text = "SmoothWheel — " + (_config.Enabled ? "Enabled" : "Disabled");
    }

    private static int ParseLeadingInt(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int n = 0, i = 0;
        while (i < s.Length && !char.IsDigit(s[i])) i++;
        while (i < s.Length && char.IsDigit(s[i])) { n = n * 10 + (s[i] - '0'); i++; }
        return n;
    }

    private static double ParseLeadingDouble(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0.0;
        int i = 0;
        while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '.' && s[i] != '-') i++;
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == '-')) i++;
        return double.TryParse(s.AsSpan(start, i - start), out double v) ? v : 0.0;
    }

    /// <summary>Build a radio-style menu item whose check state is computed fresh each refresh.</summary>
    private ToolStripMenuItem MakeRadioItem(string text, Action onClick, Func<bool> isChecked)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => onClick());
        item.CheckOnClick = false;
        item.Tag = isChecked; // retained for potential future use
        return item;
    }

    /// <summary>Generate a simple distinct icon at runtime so no .ico asset is required.</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        using var bg = new SolidBrush(Color.FromArgb(225, 70, 40));
        g.FillEllipse(bg, 2, 2, 28, 28);
        // Two "scroll arrows" to suggest wheel motion.
        var arrow = new Pen(Color.White, 2.5f) { StartCap = System.Drawing.Drawing2D.LineCap.Round,
                                                 EndCap = System.Drawing.Drawing2D.LineCap.Triangle };
        g.DrawLine(arrow, 16, 7, 16, 25);
        g.DrawLine(arrow, 10, 12, 16, 6);
        g.DrawLine(arrow, 22, 12, 16, 6);
        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _hotkey.Dispose();
        _engine.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
    }
}
