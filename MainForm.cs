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

    /// <summary>
    /// Move the mouse to (x,y) using the INPUT system (not SetCursorPos)
    /// so the game actually sees the movement. Uses fast interpolation
    /// (10 steps, ~50ms total) to simulate real mouse travel.
    /// Then clicks at the final position.
    /// </summary>
    static void ClickAt(int x, int y)
    {
        GetCursorPos(out POINT from);
        int sw = GetSystemMetrics(0);
        int sh = GetSystemMetrics(1);

        // Fast interpolated move — 10 steps over ~50ms
        const int steps = 10;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            int mx = from.X + (int)((x - from.X) * t);
            int my = from.Y + (int)((y - from.Y) * t);
            int ax = (int)((long)mx * 65535 / sw);
            int ay = (int)((long)my * 65535 / sh);

            var m = new INPUT[1];
            m[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() {
                dx = ax, dy = ay, dwFlags = MMOVE | MABS
            }}};
            SendInput(1, m, Marshal.SizeOf<INPUT>());
            if (i < steps) Thread.Sleep(5);
        }

        Thread.Sleep(40); // let the game register final position

        // Click
        var c = new INPUT[2];
        c[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dwFlags = MDOWN } } };
        c[1] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dwFlags = MUP } } };
        SendInput(2, c, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// THE MACRO — leaves a Fortnite match in 4 steps.
    ///
    /// 1. Press Escape → sidebar menu opens
    /// 2. Click exit icon (door, top-right)
    /// 3. Click "Return to lobby"
    /// 4. Click "Yes"
    ///
    /// Key rules:
    /// - Do NOT move mouse before Escape (game interprets it as camera rotation)
    /// - Use generous delays (Fortnite UI has animations)
    /// - Move mouse through input system, not SetCursorPos (game ignores teleports)
    /// </summary>
    void RunLeave(Config cfg)
    {
        try
        {
            GetCursorPos(out POINT saved);

            PressKey(0x1B); // Escape
            Thread.Sleep(cfg.EscapeDelayMs);

            ClickAt(cfg.ExitBtn[0], cfg.ExitBtn[1]);
            Thread.Sleep(cfg.ClickDelayMs);

            ClickAt(cfg.ReturnBtn[0], cfg.ReturnBtn[1]);
            Thread.Sleep(cfg.ClickDelayMs);

            ClickAt(cfg.YesBtn[0], cfg.YesBtn[1]);
            Thread.Sleep(80);

            // Restore cursor position
            SetCursorPos(saved.X, saved.Y);
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
        public int EscapeDelayMs { get; set; } = 400;
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
