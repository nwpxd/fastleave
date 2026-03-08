using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing.Drawing2D;

namespace FastLeave;

public sealed class MainForm : Form
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Win32 Interop
    // ═══════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int sz);
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
    //  Macro Core
    // ═══════════════════════════════════════════════════════════════════════

    static void PressKey(ushort vk)
    {
        var a = new INPUT[2];
        a[0] = new() { Type = INPUT_KB, U = new() { Ki = new() { wVk = vk } } };
        a[1] = new() { Type = INPUT_KB, U = new() { Ki = new() { wVk = vk, dwFlags = KEYUP } } };
        SendInput(2, a, Marshal.SizeOf<INPUT>());
    }

    static void ClickAt(int x, int y)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int ax = (int)((long)x * 65535 / sw);
        int ay = (int)((long)y * 65535 / sh);

        // Move to position
        var move = new INPUT[1];
        move[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS } } };
        SendInput(1, move, Marshal.SizeOf<INPUT>());
        Thread.Sleep(15);

        // Click with position included
        var click = new INPUT[2];
        click[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS | MDOWN } } };
        click[1] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS | MUP } } };
        SendInput(2, click, Marshal.SizeOf<INPUT>());
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

    /// Single-attempt macro. No retry, no focus stealing.
    /// Fortnite is already focused because user just pressed the hotkey in-game.
    void RunLeave(Config cfg)
    {
        try
        {
            // Small delay to let the hotkey release settle
            Thread.Sleep(30);

            // Step 1: Escape — open side panel
            PressKey(0x1B);
            if (!Wait(cfg.EscapeDelayMs)) return;

            // Step 2: Click exit door icon (top-right)
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

    sealed class Config
    {
        public uint HotkeyVk { get; set; } = 0x75;         // F6
        public int[] ExitBtn { get; set; } = [1832, 76];
        public int[] ReturnBtn { get; set; } = [1570, 384];
        public int[] YesBtn { get; set; } = [1574, 922];
        public int EscapeDelayMs { get; set; } = 600;
        public int ClickDelayMs { get; set; } = 200;
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
        public static readonly Color Bg       = Color.FromArgb(12, 12, 16);
        public static readonly Color Card     = Color.FromArgb(22, 22, 28);
        public static readonly Color CardHi   = Color.FromArgb(30, 30, 38);
        public static readonly Color Border   = Color.FromArgb(42, 42, 52);
        public static readonly Color Text     = Color.FromArgb(240, 240, 245);
        public static readonly Color Sub      = Color.FromArgb(160, 160, 175);
        public static readonly Color Dim      = Color.FromArgb(90, 90, 105);
        public static readonly Color Accent   = Color.FromArgb(90, 95, 238);
        public static readonly Color AccentHi = Color.FromArgb(120, 125, 255);
        public static readonly Color Green    = Color.FromArgb(34, 197, 94);
        public static readonly Color Red      = Color.FromArgb(239, 68, 68);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  State
    // ═══════════════════════════════════════════════════════════════════════

    readonly Config _cfg;
    bool _on = true, _capturing;
    int _running;

    readonly Label _lblKey, _lblKeyVal, _lblStatus, _lblStatusDot, _lblVer, _lblTitle;
    readonly Button _btnSet;
    readonly CheckBox _chkOn, _chkTray;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _capTimer;

    // ═══════════════════════════════════════════════════════════════════════
    //  UI — Modern dark design
    // ═══════════════════════════════════════════════════════════════════════

    public MainForm()
    {
        _cfg = LoadCfg();

        // ── Window ──
        Text = "FastLeave";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 340);
        BackColor = T.Bg;
        ForeColor = T.Text;
        Font = new Font("Segoe UI", 10f);

        LoadIcon();
        try { int v = 1; DwmSetWindowAttribute(Handle, 20, ref v, 4); } catch { }

        // ── Header with gradient accent bar ──
        var header = new Panel { Bounds = new Rectangle(0, 0, 380, 56), BackColor = T.Bg };
        header.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(
                new Point(0, 0), new Point(380, 0),
                Color.FromArgb(60, T.Accent), Color.FromArgb(0, T.Accent));
            e.Graphics.FillRectangle(brush, 0, 0, 380, 56);
        };

        _lblTitle = new Label
        {
            Text = "FastLeave",
            Font = new Font("Segoe UI Semibold", 14f),
            ForeColor = T.Text,
            BackColor = Color.Transparent,
            Location = new Point(20, 14), AutoSize = true,
        };
        header.Controls.Add(_lblTitle);

        _lblVer = new Label
        {
            Text = "v0.3.0",
            Font = new Font("Segoe UI", 8f),
            ForeColor = T.Dim,
            BackColor = Color.Transparent,
            Location = new Point(320, 20), AutoSize = true,
        };
        header.Controls.Add(_lblVer);
        Controls.Add(header);

        // ── Hotkey Card ──
        var hotkeyCard = MakeCard(20, 66, 340, 72);

        _lblKey = new Label
        {
            Text = "HOTKEY",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = T.Dim,
            BackColor = Color.Transparent,
            Location = new Point(16, 10), AutoSize = true,
        };

        _lblKeyVal = new Label
        {
            Text = VkName(_cfg.HotkeyVk),
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = T.AccentHi,
            BackColor = Color.Transparent,
            Location = new Point(14, 30), AutoSize = true,
        };

        _btnSet = new Button
        {
            Text = "Set Key",
            Font = new Font("Segoe UI Semibold", 9f),
            FlatStyle = FlatStyle.Flat,
            BackColor = T.Accent,
            ForeColor = T.Text,
            Cursor = Cursors.Hand,
            Bounds = new Rectangle(230, 22, 94, 34),
        };
        _btnSet.FlatAppearance.BorderSize = 0;
        _btnSet.FlatAppearance.MouseOverBackColor = T.AccentHi;
        _btnSet.Click += (_, _) => StartCapture();

        hotkeyCard.Controls.AddRange([_lblKey, _lblKeyVal, _btnSet]);
        Controls.Add(hotkeyCard);

        // ── Settings Card ──
        var settingsCard = MakeCard(20, 150, 340, 88);

        var lblSettings = new Label
        {
            Text = "SETTINGS",
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = T.Dim,
            BackColor = Color.Transparent,
            Location = new Point(16, 10), AutoSize = true,
        };

        _chkOn = Chk("Enabled", 16, 34, true);
        _chkOn.CheckedChanged += (_, _) => { _on = _chkOn.Checked; UpdateStatus(); };

        _chkTray = Chk("Minimize to tray", 16, 58, _cfg.MinimizeToTray);
        _chkTray.CheckedChanged += (_, _) => { _cfg.MinimizeToTray = _chkTray.Checked; SaveCfg(_cfg); };

        settingsCard.Controls.AddRange([lblSettings, _chkOn, _chkTray]);
        Controls.Add(settingsCard);

        // ── Status Card ──
        var statusCard = MakeCard(20, 250, 340, 56);

        _lblStatusDot = new Label
        {
            Text = "\u25CF",
            Font = new Font("Segoe UI", 14f),
            BackColor = Color.Transparent,
            Location = new Point(14, 12), AutoSize = true,
        };

        _lblStatus = new Label
        {
            Font = new Font("Segoe UI Semibold", 12f),
            BackColor = Color.Transparent,
            Location = new Point(38, 16), AutoSize = true,
        };
        UpdateStatus();

        statusCard.Controls.AddRange([_lblStatusDot, _lblStatus]);
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
            _lblStatus.ForeColor = T.Red;
            _lblStatusDot.ForeColor = T.Red;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  UI Helpers
    // ═══════════════════════════════════════════════════════════════════════

    Panel MakeCard(int x, int y, int w, int h)
    {
        var card = new Panel
        {
            Bounds = new Rectangle(x, y, w, h),
            BackColor = T.Card,
        };
        card.Paint += (_, e) =>
        {
            var r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using var path = RoundedRect(r, 10);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(T.Card);
            e.Graphics.FillPath(fill, path);
            using var pen = new Pen(T.Border, 1);
            e.Graphics.DrawPath(pen, path);
        };
        card.Region = CreateRoundRegion(w, h, 10);
        return card;
    }

    CheckBox Chk(string text, int x, int y, bool on) => new()
    {
        Text = text, Location = new Point(x, y), Size = new Size(240, 22),
        Checked = on, ForeColor = T.Sub, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", 9.5f),
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
        _lblStatus.ForeColor = _on ? T.Green : T.Red;
        _lblStatusDot.ForeColor = _on ? T.Green : T.Red;
    }

    static GraphicsPath RoundedRect(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
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
        _btnSet.Text = "Press...";
        _btnSet.BackColor = T.AccentHi;
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
                _btnSet.BackColor = T.Accent;
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
