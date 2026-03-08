using System.Runtime.InteropServices;
using System.Text.Json;
using Guna.UI2.WinForms;

namespace FastLeave;

public sealed class MainForm : Form
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Win32 Interop
    // ═══════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int sz);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr extra);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, uint a, ref int v, int s);

    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public INPUTUNION U; }
    [StructLayout(LayoutKind.Explicit)]   struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
    }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra;
    }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT
    {
        public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra;
    }

    const uint INPUT_KB = 1;
    const uint KEYUP = 0x0002;
    const int WM_HOTKEY = 0x0312, HK_ID = 1;

    // ═══════════════════════════════════════════════════════════════════════
    //  Macro Core — UNTOUCHED BACKEND LOGIC
    // ═══════════════════════════════════════════════════════════════════════

    static void PressKey(ushort vk)
    {
        ushort scan = (ushort)MapVirtualKey(vk, 0);
        var a = new INPUT[2];
        a[0] = new() { Type = INPUT_KB, U = new() { Ki = new() { wVk = vk, wScan = scan } } };
        a[1] = new() { Type = INPUT_KB, U = new() { Ki = new() { wVk = vk, wScan = scan, dwFlags = KEYUP } } };
        SendInput(2, a, Marshal.SizeOf<INPUT>());
    }

    static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(15);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // LEFTDOWN
        Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // LEFTUP
    }

    static bool UserInterrupted()
    {
        int[] keys = [0x01, 0x02, 0x57, 0x41, 0x53, 0x44, 0x25, 0x26, 0x27, 0x28, 0x1B, 0x20];
        foreach (int k in keys)
            if ((GetAsyncKeyState(k) & 0x8000) != 0) return true;
        return false;
    }

    static bool Wait(int ms)
    {
        int elapsed = 0;
        while (elapsed < ms)
        {
            Thread.Sleep(10);
            elapsed += 10;
            if (UserInterrupted()) return false;
        }
        return true;
    }

    void RunLeave(Config cfg)
    {
        try
        {
            Thread.Sleep(50);

            // Step 1: Escape — open side panel
            PressKey(0x1B);
            if (!Wait(cfg.EscapeDelayMs)) return;

            // Step 2: Click exit door icon
            ClickAt(cfg.ExitBtn[0], cfg.ExitBtn[1]);
            if (!Wait(cfg.ClickDelayMs)) return;

            // Step 3: Click "Return to lobby"
            ClickAt(cfg.ReturnBtn[0], cfg.ReturnBtn[1]);
            if (!Wait(cfg.ClickDelayMs)) return;

            // Step 4: Click "Yes"
            ClickAt(cfg.YesBtn[0], cfg.YesBtn[1]);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Config
    // ═══════════════════════════════════════════════════════════════════════

    const int CFG_VERSION = 6;

    sealed class Config
    {
        public int Version { get; set; } = CFG_VERSION;
        public uint HotkeyVk { get; set; } = 0x75;
        public int[] ExitBtn { get; set; } = [1832, 76];
        public int[] ReturnBtn { get; set; } = [1570, 384];
        public int[] YesBtn { get; set; } = [1574, 922];
        public int EscapeDelayMs { get; set; } = 800;
        public int ClickDelayMs { get; set; } = 300;
        public bool MinimizeToTray { get; set; } = true;
    }

    static readonly JsonSerializerOptions _jo = new() { WriteIndented = true };

    static string CfgPath()
    {
        var d = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        return Path.Combine(d, "fastleave.json");
    }

    static Config LoadCfg()
    {
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(CfgPath()), _jo) ?? new();
            if (c.Version < CFG_VERSION)
            {
                var fresh = new Config { HotkeyVk = c.HotkeyVk, MinimizeToTray = c.MinimizeToTray };
                SaveCfg(fresh);
                return fresh;
            }
            return c;
        }
        catch { var c = new Config(); SaveCfg(c); return c; }
    }

    static void SaveCfg(Config c)
    {
        try { File.WriteAllText(CfgPath(), JsonSerializer.Serialize(c, _jo)); } catch { }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  VK names
    // ═══════════════════════════════════════════════════════════════════════

    static string VkName(uint vk) => vk switch
    {
        0x08 => "Backspace", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Escape",
        0x20 => "Space", 0x21 => "PgUp", 0x22 => "PgDn", 0x23 => "End",
        0x24 => "Home", 0x2D => "Ins", 0x2E => "Del", 0x13 => "Pause",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num{vk - 0x60}",
        >= 0x70 and <= 0x7B => $"F{vk - 0x70 + 1}",
        _ => $"0x{vk:X2}"
    };

    static readonly int[] ScanVks = [
        ..Enumerable.Range(0x70, 12), ..Enumerable.Range(0x30, 10),
        ..Enumerable.Range(0x41, 26), ..Enumerable.Range(0x60, 10),
        0x21, 0x22, 0x23, 0x24, 0x2D, 0x2E, 0x13, 0x08, 0x09, 0x20
    ];

    // ═══════════════════════════════════════════════════════════════════════
    //  Theme Colors
    // ═══════════════════════════════════════════════════════════════════════

    static readonly Color C_Bg      = ColorTranslator.FromHtml("#1E1E1E");
    static readonly Color C_Card    = ColorTranslator.FromHtml("#252526");
    static readonly Color C_Border  = ColorTranslator.FromHtml("#333333");
    static readonly Color C_Text    = ColorTranslator.FromHtml("#E0E0E0");
    static readonly Color C_Sub     = ColorTranslator.FromHtml("#9E9E9E");
    static readonly Color C_Dim     = ColorTranslator.FromHtml("#666666");
    static readonly Color C_Accent  = ColorTranslator.FromHtml("#5C5CFF");
    static readonly Color C_AccHi   = ColorTranslator.FromHtml("#7B7BFF");
    static readonly Color C_Green   = ColorTranslator.FromHtml("#22C55E");
    static readonly Color C_Red     = ColorTranslator.FromHtml("#EF4444");

    // ═══════════════════════════════════════════════════════════════════════
    //  State
    // ═══════════════════════════════════════════════════════════════════════

    readonly Config _cfg;
    bool _on = true, _capturing;
    int _running;

    readonly Label _lblKeyHeader, _lblKeyVal, _lblStatus, _lblSettingsHeader;
    readonly Guna2Button _btnSet;
    readonly Guna2ToggleSwitch _togOn, _togTray;
    readonly Label _lblTogOn, _lblTogTray;
    readonly Guna2CirclePictureBox _statusDot;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _capTimer;

    // ═══════════════════════════════════════════════════════════════════════
    //  UI — Guna2 Modern Dark
    // ═══════════════════════════════════════════════════════════════════════

    public MainForm()
    {
        _cfg = LoadCfg();

        // ── Borderless dark form ──
        Text = "FastLeave";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 380);
        BackColor = C_Bg;
        ForeColor = C_Text;
        Font = new Font("Segoe UI Variable Display", 10f);
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;

        LoadIcon();

        // Rounded corners via DWM (Windows 11)
        try
        {
            int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
        }
        catch { }

        // ── Title bar area (draggable) ──
        var titleBar = new Guna2Panel
        {
            Bounds = new Rectangle(0, 0, 400, 52),
            BackColor = C_Bg,
            BorderRadius = 0,
        };
        titleBar.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };

        var lblTitle = new Label
        {
            Text = "FastLeave",
            Font = new Font("Segoe UI Variable Display", 13f, FontStyle.Bold),
            ForeColor = C_Text,
            BackColor = Color.Transparent,
            Location = new Point(20, 14), AutoSize = true,
        };

        var lblVer = new Label
        {
            Text = "v0.4.1",
            Font = new Font("Segoe UI Variable Display", 8f),
            ForeColor = C_Dim,
            BackColor = Color.Transparent,
            Location = new Point(312, 18), AutoSize = true,
        };

        var btnClose = new Guna2Button
        {
            Text = "\u2715",
            Font = new Font("Segoe UI", 10f),
            ForeColor = C_Sub,
            FillColor = Color.Transparent,
            HoverState = { FillColor = C_Red, ForeColor = Color.White },
            BorderRadius = 6,
            Size = new Size(32, 32),
            Location = new Point(358, 10),
        };
        btnClose.Click += (_, _) => Close();

        titleBar.Controls.AddRange([lblTitle, lblVer, btnClose]);
        Controls.Add(titleBar);

        // ── Hotkey Card ──
        var hotkeyCard = MakeCard(20, 60, 360, 80);

        _lblKeyHeader = new Label
        {
            Text = "HOTKEY",
            Font = new Font("Segoe UI Variable Display", 7.5f, FontStyle.Bold),
            ForeColor = C_Dim,
            BackColor = Color.Transparent,
            Location = new Point(16, 12), AutoSize = true,
        };

        _lblKeyVal = new Label
        {
            Text = VkName(_cfg.HotkeyVk),
            Font = new Font("Segoe UI Variable Display", 18f, FontStyle.Bold),
            ForeColor = C_Accent,
            BackColor = Color.Transparent,
            Location = new Point(14, 34), AutoSize = true,
        };

        _btnSet = new Guna2Button
        {
            Text = "Set Key",
            Font = new Font("Segoe UI Variable Display", 9f, FontStyle.Bold),
            ForeColor = Color.White,
            FillColor = C_Accent,
            HoverState = { FillColor = C_AccHi },
            BorderRadius = 8,
            Size = new Size(100, 36),
            Location = new Point(244, 24),
            Cursor = Cursors.Hand,
        };
        _btnSet.Click += (_, _) => StartCapture();

        hotkeyCard.Controls.AddRange([_lblKeyHeader, _lblKeyVal, _btnSet]);
        Controls.Add(hotkeyCard);

        // ── Settings Card ──
        var settingsCard = MakeCard(20, 152, 360, 112);

        _lblSettingsHeader = new Label
        {
            Text = "SETTINGS",
            Font = new Font("Segoe UI Variable Display", 7.5f, FontStyle.Bold),
            ForeColor = C_Dim,
            BackColor = Color.Transparent,
            Location = new Point(16, 12), AutoSize = true,
        };

        // Enabled toggle
        _lblTogOn = new Label
        {
            Text = "Enabled",
            Font = new Font("Segoe UI Variable Display", 9.5f),
            ForeColor = C_Text,
            BackColor = Color.Transparent,
            Location = new Point(16, 40), AutoSize = true,
        };

        _togOn = new Guna2ToggleSwitch
        {
            Checked = true,
            Location = new Point(296, 38),
            Size = new Size(48, 24),
            CheckedState = { FillColor = C_Accent, InnerColor = Color.White },
            UncheckedState = { FillColor = C_Border, InnerColor = C_Sub },
        };
        _togOn.CheckedChanged += (_, _) => { _on = _togOn.Checked; UpdateStatus(); };

        // Tray toggle
        _lblTogTray = new Label
        {
            Text = "Minimize to tray",
            Font = new Font("Segoe UI Variable Display", 9.5f),
            ForeColor = C_Text,
            BackColor = Color.Transparent,
            Location = new Point(16, 72), AutoSize = true,
        };

        _togTray = new Guna2ToggleSwitch
        {
            Checked = _cfg.MinimizeToTray,
            Location = new Point(296, 70),
            Size = new Size(48, 24),
            CheckedState = { FillColor = C_Accent, InnerColor = Color.White },
            UncheckedState = { FillColor = C_Border, InnerColor = C_Sub },
        };
        _togTray.CheckedChanged += (_, _) => { _cfg.MinimizeToTray = _togTray.Checked; SaveCfg(_cfg); };

        settingsCard.Controls.AddRange([_lblSettingsHeader, _lblTogOn, _togOn, _lblTogTray, _togTray]);
        Controls.Add(settingsCard);

        // ── Status Card ──
        var statusCard = MakeCard(20, 276, 360, 60);

        _statusDot = new Guna2CirclePictureBox
        {
            Size = new Size(14, 14),
            Location = new Point(18, 23),
            ShadowDecoration = { Enabled = false },
        };

        _lblStatus = new Label
        {
            Font = new Font("Segoe UI Variable Display", 12f, FontStyle.Bold),
            BackColor = Color.Transparent,
            Location = new Point(40, 18), AutoSize = true,
        };
        UpdateStatus();

        statusCard.Controls.AddRange([_statusDot, _lblStatus]);
        Controls.Add(statusCard);

        // ── Tray ──
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => Restore());
        menu.Items.Add("-");
        menu.Items.Add("Quit", null, (_, _) => { _tray!.Visible = false; Application.Exit(); });

        _tray = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "FastLeave",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => Restore();

        // ── Key capture timer ──
        _capTimer = new System.Windows.Forms.Timer { Interval = 40 };
        _capTimer.Tick += CaptureKey;

        // ── Register hotkey ──
        if (!RegisterHotKey(Handle, HK_ID, 0, _cfg.HotkeyVk))
        {
            _lblStatus.Text = "HOTKEY CONFLICT";
            _lblStatus.ForeColor = C_Red;
            _statusDot.BackColor = C_Red;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Drag support
    // ═══════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, int wp, int lp);

    // ═══════════════════════════════════════════════════════════════════════
    //  UI Helpers
    // ═══════════════════════════════════════════════════════════════════════

    Guna2Panel MakeCard(int x, int y, int w, int h) => new()
    {
        Bounds = new Rectangle(x, y, w, h),
        FillColor = C_Card,
        BorderRadius = 8,
        BorderColor = C_Border,
        BorderThickness = 1,
    };

    void LoadIcon()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        foreach (var p in new[] { Path.Combine(dir, "icon.ico"), "icon.ico" })
            if (File.Exists(p)) { try { Icon = new Icon(p); return; } catch { } }
    }

    void UpdateStatus()
    {
        _lblStatus.Text = _on ? "READY" : "DISABLED";
        _lblStatus.ForeColor = _on ? C_Green : C_Red;
        _statusDot.BackColor = _on ? C_Green : C_Red;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Tray
    // ═══════════════════════════════════════════════════════════════════════

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _cfg.MinimizeToTray)
        {
            Hide();
            _tray.Visible = true;
        }
    }

    void Restore()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Hotkey dispatch
    // ═══════════════════════════════════════════════════════════════════════

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam == HK_ID &&
            _on && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            Task.Run(() => RunLeave(_cfg));
        }
        base.WndProc(ref m);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Key capture
    // ═══════════════════════════════════════════════════════════════════════

    void StartCapture()
    {
        _capturing = true;
        _btnSet.Text = "Press...";
        _btnSet.FillColor = C_AccHi;
        UnregisterHotKey(Handle, HK_ID);
        _capTimer.Start();
    }

    void CaptureKey(object? s, EventArgs e)
    {
        if (!_capturing) return;
        foreach (int vk in ScanVks)
        {
            if ((GetAsyncKeyState(vk) & 1) != 0)
            {
                _cfg.HotkeyVk = (uint)vk;
                SaveCfg(_cfg);
                RegisterHotKey(Handle, HK_ID, 0, _cfg.HotkeyVk);
                _lblKeyVal.Text = VkName(_cfg.HotkeyVk);
                _btnSet.Text = "Set Key";
                _btnSet.FillColor = C_Accent;
                _capturing = false;
                _capTimer.Stop();
                return;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Cleanup
    // ═══════════════════════════════════════════════════════════════════════

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        UnregisterHotKey(Handle, HK_ID);
        _capTimer.Stop();
        _capTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
        Environment.Exit(0);
    }
}
