using System.Runtime.InteropServices;
using System.Text.Json;

namespace FastLeave;

public sealed class MainForm : Form
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Win32 Interop
    // ═══════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int sz);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, uint a, ref int v, int s);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
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

    const uint INPUT_KB = 1, INPUT_MOUSE = 0;
    const uint KEYUP = 0x0002, MDOWN = 0x0002, MUP = 0x0004;
    const uint MMOVE = 0x0001, MABS = 0x8000;
    const int WM_HOTKEY = 0x0312, HK_ID = 1;

    // ═══════════════════════════════════════════════════════════════════════
    //  Input — The macro core. This is the part that matters.
    // ═══════════════════════════════════════════════════════════════════════

    static void PressKey(ushort vk)
    {
        var a = new INPUT[2];
        a[0] = new() { Type = INPUT_KB, U = new() { Ki = new() { wVk = vk } } };
        a[1] = new() { Type = INPUT_KB, U = new() { Ki = new() { wVk = vk, dwFlags = KEYUP } } };
        SendInput(2, a, Marshal.SizeOf<INPUT>());
    }

    /// Move cursor to (x,y) and click.
    static void ClickAt(int x, int y)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int ax = (int)((long)x * 65535 / sw);
        int ay = (int)((long)y * 65535 / sh);

        // Move to target position
        var move = new INPUT[1];
        move[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS } } };
        SendInput(1, move, Marshal.SizeOf<INPUT>());
        Thread.Sleep(20);

        // Click at current position
        var click = new INPUT[2];
        click[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS | MDOWN } } };
        click[1] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS | MUP } } };
        SendInput(2, click, Marshal.SizeOf<INPUT>());
    }

    /// Returns true if user is touching mouse or keyboard — macro should abort.
    static bool UserInterrupted()
    {
        // Mouse buttons, WASD, arrows, Escape, Space
        int[] keys = [0x01, 0x02, 0x57, 0x41, 0x53, 0x44, 0x25, 0x26, 0x27, 0x28, 0x1B, 0x20];
        foreach (int k in keys)
            if ((GetAsyncKeyState(k) & 0x8000) != 0) return true;
        return false;
    }

    /// Sleep in 15ms chunks, abort early if user does anything.
    static bool Wait(int ms)
    {
        int elapsed = 0;
        while (elapsed < ms)
        {
            Thread.Sleep(15);
            elapsed += 15;
            if (UserInterrupted()) return false;
        }
        return true;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

    /// Focus Fortnite before sending inputs.
    static void FocusFortnite()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() == "UnrealWindow")
            {
                var title = new System.Text.StringBuilder(256);
                GetWindowText(h, title, 256);
                if (title.ToString().Contains("Fortnite", StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero && GetForegroundWindow() != found)
        {
            SetForegroundWindow(found);
            Thread.Sleep(80);
        }
    }

    /// Run one full leave attempt. Returns true if not interrupted.
    static bool DoLeaveAttempt(Config cfg, int escDelay)
    {
        PressKey(0x1B);
        if (!Wait(escDelay)) return false;

        ClickAt(cfg.ExitBtn[0], cfg.ExitBtn[1]);
        if (!Wait(cfg.ClickDelayMs)) return false;

        ClickAt(cfg.ReturnBtn[0], cfg.ReturnBtn[1]);
        if (!Wait(cfg.ClickDelayMs)) return false;

        ClickAt(cfg.YesBtn[0], cfg.YesBtn[1]);
        return true;
    }

    /// THE MACRO — runs the sequence twice to handle cases where
    /// first Escape closes a sub-state (build mode, inventory, map)
    /// instead of opening the leave menu.
    void RunLeave(Config cfg)
    {
        try
        {
            // Focus Fortnite first so inputs go to the game
            FocusFortnite();

            // Attempt 1: Full sequence
            if (!DoLeaveAttempt(cfg, cfg.EscapeDelayMs)) return;

            // Brief pause then attempt 2: catches the case where
            // attempt 1's Escape only closed a sub-menu.
            // If attempt 1 already worked, we're in loading screen
            // and these inputs do nothing harmful.
            if (!Wait(200)) return;
            DoLeaveAttempt(cfg, cfg.EscapeDelayMs);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Config
    // ═══════════════════════════════════════════════════════════════════════

    sealed class Config
    {
        public uint HotkeyVk { get; set; } = 0x75;         // F6
        public int[] ExitBtn { get; set; } = [1832, 76];
        public int[] ReturnBtn { get; set; } = [1570, 384];
        public int[] YesBtn { get; set; } = [1574, 922];
        public int EscapeDelayMs { get; set; } = 700;
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
        try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(CfgPath()), _jo) ?? new(); }
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
    //  Theme
    // ═══════════════════════════════════════════════════════════════════════

    static class T
    {
        public static readonly Color Bg      = Color.FromArgb(18, 18, 22);
        public static readonly Color Card    = Color.FromArgb(28, 28, 34);
        public static readonly Color Border  = Color.FromArgb(48, 48, 56);
        public static readonly Color Text    = Color.FromArgb(235, 235, 240);
        public static readonly Color Dim     = Color.FromArgb(120, 120, 130);
        public static readonly Color Accent  = Color.FromArgb(99, 102, 241);
        public static readonly Color Green   = Color.FromArgb(34, 197, 94);
        public static readonly Color Red     = Color.FromArgb(239, 68, 68);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  State
    // ═══════════════════════════════════════════════════════════════════════

    readonly Config _cfg;
    bool _on = true, _capturing;
    int _running;

    readonly Label _lblKey, _lblStatus, _lblVer;
    readonly Button _btnSet;
    readonly CheckBox _chkOn, _chkTray;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _capTimer;

    // ═══════════════════════════════════════════════════════════════════════
    //  UI
    // ═══════════════════════════════════════════════════════════════════════

    public MainForm()
    {
        _cfg = LoadCfg();

        // ── Window ──
        Text = "FastLeave";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(360, 220);
        BackColor = T.Bg;
        ForeColor = T.Text;
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(24, 20, 24, 12);

        LoadIcon();
        try { int v = 1; DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }

        // ── Card panel ──
        var card = new Panel
        {
            Bounds = new Rectangle(16, 12, 328, 152),
            BackColor = T.Card,
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(T.Border, 1);
            var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            int radius = 12;
            using var path = RoundedRect(r, radius);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(new SolidBrush(T.Card), path);
            e.Graphics.DrawPath(pen, path);
        };
        card.Region = CreateRoundRegion(328, 152, 12);

        // ── Row 1: Hotkey ──
        _lblKey = new Label
        {
            Text = $"Hotkey   {VkName(_cfg.HotkeyVk)}",
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = T.Text,
            BackColor = Color.Transparent,
            Location = new Point(16, 14), Size = new Size(190, 26),
        };

        _btnSet = new Button
        {
            Text = "Set Key",
            Font = new Font("Segoe UI", 9f),
            FlatStyle = FlatStyle.Flat,
            BackColor = T.Border,
            ForeColor = T.Text,
            Cursor = Cursors.Hand,
            Bounds = new Rectangle(216, 12, 96, 30),
        };
        _btnSet.FlatAppearance.BorderSize = 0;
        _btnSet.FlatAppearance.MouseOverBackColor = T.Accent;
        _btnSet.Click += (_, _) => StartCapture();

        // ── Divider ──
        var div = new Panel
        {
            BackColor = T.Border, Bounds = new Rectangle(16, 52, 296, 1),
        };

        // ── Row 2: Enabled ──
        _chkOn = Chk("Enabled", 16, 62, true);
        _chkOn.CheckedChanged += (_, _) => { _on = _chkOn.Checked; Status(); };

        // ── Row 3: Tray ──
        _chkTray = Chk("Minimize to tray", 16, 92, _cfg.MinimizeToTray);
        _chkTray.CheckedChanged += (_, _) => { _cfg.MinimizeToTray = _chkTray.Checked; SaveCfg(_cfg); };

        // ── Status ──
        _lblStatus = new Label
        {
            Font = new Font("Segoe UI Semibold", 11f),
            BackColor = Color.Transparent,
            Location = new Point(16, 122), Size = new Size(296, 26),
        };
        Status();

        card.Controls.AddRange([_lblKey, _btnSet, div, _chkOn, _chkTray, _lblStatus]);
        Controls.Add(card);

        // ── Version ──
        _lblVer = new Label
        {
            Text = "v1.0.0",
            Font = new Font("Segoe UI", 7.5f),
            ForeColor = T.Dim,
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.BottomRight,
            Bounds = new Rectangle(270, 172, 80, 20),
        };
        Controls.Add(_lblVer);

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

        // ── Hotkey ──
        if (!RegisterHotKey(Handle, HK_ID, 0, _cfg.HotkeyVk))
        {
            _lblStatus.Text = "HOTKEY CONFLICT";
            _lblStatus.ForeColor = T.Red;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    CheckBox Chk(string text, int x, int y, bool on) => new()
    {
        Text = text, Location = new Point(x, y), Size = new Size(220, 24),
        Checked = on, ForeColor = T.Text, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 9.5f),
    };

    void LoadIcon()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
        foreach (var p in new[] { Path.Combine(dir, "icon.ico"), "icon.ico" })
            if (File.Exists(p)) { try { Icon = new Icon(p); return; } catch { } }
    }

    void Status()
    {
        _lblStatus.Text = _on ? "READY" : "DISABLED";
        _lblStatus.ForeColor = _on ? T.Green : T.Red;
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Region CreateRoundRegion(int w, int h, int r)
    {
        using var p = RoundedRect(new Rectangle(0, 0, w, h), r);
        return new Region(p);
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
        _btnSet.Text = "Press key...";
        _btnSet.BackColor = T.Accent;
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
                _lblKey.Text = $"Hotkey   {VkName(_cfg.HotkeyVk)}";
                _btnSet.Text = "Set Key";
                _btnSet.BackColor = T.Border;
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
