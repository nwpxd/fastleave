using System.Runtime.InteropServices;
using System.Text.Json;
using Guna.UI2.WinForms;

namespace FastLeave;

public sealed class MainForm : Form
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Win32 Interop
    // ═══════════════════════════════════════════════════════════════════════

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int sz);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, char[] buf, int max);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
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

    const uint INPUT_KB = 1, INPUT_MOUSE = 0;
    const uint KEYUP = 0x0002;
    const uint MDOWN = 0x0002, MUP = 0x0004, MMOVE = 0x0001, MABS = 0x8000;

    // ═══════════════════════════════════════════════════════════════════════
    //  Macro Core
    // ═══════════════════════════════════════════════════════════════════════

    static IntPtr FindFortnite()
    {
        IntPtr found = IntPtr.Zero;
        var buf = new char[256];
        EnumWindows((hWnd, _) =>
        {
            GetClassName(hWnd, buf, buf.Length);
            if (new string(buf).TrimEnd('\0') == "UnrealWindow")
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static bool IsFortniteActive()
    {
        var fn = FindFortnite();
        return fn != IntPtr.Zero && GetForegroundWindow() == fn;
    }

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
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int ax = (int)((long)x * 65535 / sw);
        int ay = (int)((long)y * 65535 / sh);
        int sz = Marshal.SizeOf<INPUT>();

        var move = new INPUT[1];
        move[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS } } };
        SendInput(1, move, sz);
        Thread.Sleep(15);

        var down = new INPUT[1];
        down[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS | MDOWN } } };
        SendInput(1, down, sz);
        Thread.Sleep(30);

        var up = new INPUT[1];
        up[0] = new() { Type = INPUT_MOUSE, U = new() { Mi = new() { dx = ax, dy = ay, dwFlags = MMOVE | MABS | MUP } } };
        SendInput(1, up, sz);
    }

    void RunLeave(Config cfg)
    {
        try
        {
            // User pressed Escape — wait for menu, then click through.
            // Single clean pass. All delays are user-tunable.
            Thread.Sleep(cfg.StartDelayMs);

            ClickAt(cfg.ExitBtn[0], cfg.ExitBtn[1]);
            Thread.Sleep(cfg.ClickDelayMs);

            ClickAt(cfg.ReturnBtn[0], cfg.ReturnBtn[1]);
            Thread.Sleep(cfg.ClickDelayMs);

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

    const int CFG_VERSION = 8;

    sealed class Config
    {
        public int Version { get; set; } = CFG_VERSION;
        public int[] ExitBtn { get; set; } = [1832, 76];
        public int[] ReturnBtn { get; set; } = [1570, 384];
        public int[] YesBtn { get; set; } = [1574, 922];
        public int StartDelayMs { get; set; } = 500;
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
        try
        {
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(CfgPath()), _jo) ?? new();
            if (c.Version < CFG_VERSION)
            {
                var fresh = new Config { MinimizeToTray = c.MinimizeToTray };
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
    //  Theme Colors
    // ═══════════════════════════════════════════════════════════════════════

    static readonly Color C_Bg      = Color.White;
    static readonly Color C_Text    = ColorTranslator.FromHtml("#111827");
    static readonly Color C_Muted   = ColorTranslator.FromHtml("#9CA3AF");
    static readonly Color C_Light   = ColorTranslator.FromHtml("#E5E7EB");
    static readonly Color C_Hover   = ColorTranslator.FromHtml("#F3F4F6");
    static readonly Color C_Green   = ColorTranslator.FromHtml("#22C55E");
    static readonly Color C_Red     = ColorTranslator.FromHtml("#EF4444");
    static readonly Color C_Border  = ColorTranslator.FromHtml("#F0F0F0");

    // ═══════════════════════════════════════════════════════════════════════
    //  State
    // ═══════════════════════════════════════════════════════════════════════

    readonly Config _cfg;
    bool _on = true;
    int _running;

    readonly Label _lblStatus;
    readonly Guna2ToggleSwitch _togOn, _togTray;
    readonly Guna2CirclePictureBox _statusDot;
    readonly Guna2TextBox _txtStart, _txtClick;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _escTimer;

    // ═══════════════════════════════════════════════════════════════════════
    //  UI
    // ═══════════════════════════════════════════════════════════════════════

    public MainForm()
    {
        _cfg = LoadCfg();

        Text = "FastLeave";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 480);
        BackColor = C_Bg;
        ForeColor = C_Text;
        Font = new Font("Segoe UI Variable Display", 10f);
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        Padding = new Padding(0);

        LoadIcon();

        try
        {
            int dark = 0; DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
        }
        catch { }

        const int L = 32;  // global left margin
        const int R = 340; // right-align anchor for controls

        // ── Custom chrome ──
        var titleBar = new Guna2Panel
        {
            Bounds = new Rectangle(0, 0, 380, 60),
            BackColor = C_Bg,
            BorderRadius = 0,
        };
        titleBar.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };

        var lblTitle = new Label
        {
            Text = "FastLeave",
            Font = new Font("Segoe UI Variable Display", 15f, FontStyle.Bold),
            ForeColor = C_Text,
            BackColor = Color.Transparent,
            Location = new Point(L, 18), AutoSize = true,
        };

        var lblVer = new Label
        {
            Text = "v1.4.1",
            Font = new Font("Segoe UI Variable Display", 8f),
            ForeColor = C_Muted,
            BackColor = Color.Transparent,
            Location = new Point(130, 25), AutoSize = true,
        };

        var cbMin = new Guna2ControlBox
        {
            ControlBoxType = Guna.UI2.WinForms.Enums.ControlBoxType.MinimizeBox,
            IconColor = C_Text,
            FillColor = C_Bg,
            HoverState = { FillColor = C_Hover, IconColor = C_Text },
            Size = new Size(36, 28),
            Location = new Point(296, 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        var cbClose = new Guna2ControlBox
        {
            ControlBoxType = Guna.UI2.WinForms.Enums.ControlBoxType.CloseBox,
            IconColor = C_Text,
            FillColor = C_Bg,
            HoverState = { FillColor = C_Red, IconColor = Color.White },
            Size = new Size(36, 28),
            Location = new Point(334, 16),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        titleBar.Controls.AddRange([lblTitle, lblVer, cbMin, cbClose]);
        Controls.Add(titleBar);

        // ── Divider under title ──
        Controls.Add(MakeDivider(L, 62));

        // ── Trigger section ──
        Controls.Add(MakeHeader("TRIGGER", L, 76));

        Controls.Add(new Label
        {
            Text = "ESC",
            Font = new Font("Segoe UI Variable Display", 32f, FontStyle.Bold),
            ForeColor = C_Text, BackColor = C_Bg,
            Location = new Point(L - 4, 96), AutoSize = true,
        });

        Controls.Add(new Label
        {
            Text = "Press Escape in-game to leave match",
            Font = new Font("Segoe UI Variable Display", 8.5f),
            ForeColor = C_Muted, BackColor = C_Bg,
            Location = new Point(L, 146), AutoSize = true,
        });

        // ── Divider ──
        Controls.Add(MakeDivider(L, 174));

        // ── Timing section ──
        Controls.Add(MakeHeader("TIMING", L, 188));

        Controls.Add(MakeLabel("Start delay", L, 216));
        Controls.Add(MakeSuffix("ms", R, 218));
        _txtStart = MakeNumBox(_cfg.StartDelayMs, R - 68, 210);
        _txtStart.TextChanged += (_, _) => { if (int.TryParse(_txtStart.Text, out int v) && v >= 0) { _cfg.StartDelayMs = v; SaveCfg(_cfg); } };
        Controls.Add(_txtStart);

        Controls.Add(MakeLabel("Click delay", L, 254));
        Controls.Add(MakeSuffix("ms", R, 256));
        _txtClick = MakeNumBox(_cfg.ClickDelayMs, R - 68, 248);
        _txtClick.TextChanged += (_, _) => { if (int.TryParse(_txtClick.Text, out int v) && v >= 0) { _cfg.ClickDelayMs = v; SaveCfg(_cfg); } };
        Controls.Add(_txtClick);

        // ── Divider ──
        Controls.Add(MakeDivider(L, 290));

        // ── Settings section ──
        Controls.Add(MakeHeader("SETTINGS", L, 304));

        Controls.Add(MakeLabel("Enabled", L, 332));
        _togOn = new Guna2ToggleSwitch
        {
            Checked = true,
            Location = new Point(R - 44, 330),
            Size = new Size(44, 22),
            CheckedState = { FillColor = C_Text, InnerColor = Color.White },
            UncheckedState = { FillColor = C_Light, InnerColor = Color.White },
        };
        _togOn.CheckedChanged += (_, _) => { _on = _togOn.Checked; UpdateStatus(); };
        Controls.Add(_togOn);

        Controls.Add(MakeLabel("Minimize to tray", L, 368));
        _togTray = new Guna2ToggleSwitch
        {
            Checked = _cfg.MinimizeToTray,
            Location = new Point(R - 44, 366),
            Size = new Size(44, 22),
            CheckedState = { FillColor = C_Text, InnerColor = Color.White },
            UncheckedState = { FillColor = C_Light, InnerColor = Color.White },
        };
        _togTray.CheckedChanged += (_, _) => { _cfg.MinimizeToTray = _togTray.Checked; SaveCfg(_cfg); };
        Controls.Add(_togTray);

        // ── Divider ──
        Controls.Add(MakeDivider(L, 404));

        // ── Status ──
        _statusDot = new Guna2CirclePictureBox
        {
            Size = new Size(10, 10),
            Location = new Point(L, 428),
            BackColor = C_Bg,
            ShadowDecoration = { Enabled = false },
        };
        Controls.Add(_statusDot);

        _lblStatus = new Label
        {
            Font = new Font("Segoe UI Variable Display", 9f, FontStyle.Bold),
            BackColor = C_Bg,
            Location = new Point(L + 18, 423), AutoSize = true,
        };
        UpdateStatus();
        Controls.Add(_lblStatus);

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

        // ── Escape poll (30ms) ──
        _escTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _escTimer.Tick += PollEscape;
        _escTimer.Start();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Escape key polling
    // ═══════════════════════════════════════════════════════════════════════

    void PollEscape(object? s, EventArgs e)
    {
        if (_on && (GetAsyncKeyState(0x1B) & 1) != 0 &&
            Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            Task.Run(() => RunLeave(_cfg));
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

    static Label MakeHeader(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Variable Display", 7f, FontStyle.Bold),
        ForeColor = C_Muted, BackColor = C_Bg,
        Location = new Point(x, y), AutoSize = true,
    };

    static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Variable Display", 9.5f),
        ForeColor = C_Text, BackColor = C_Bg,
        Location = new Point(x, y), AutoSize = true,
    };

    static Label MakeSuffix(string text, int x, int y) => new()
    {
        Text = text,
        Font = new Font("Segoe UI Variable Display", 8f),
        ForeColor = C_Muted, BackColor = C_Bg,
        Location = new Point(x, y), AutoSize = true,
    };

    static Guna2TextBox MakeNumBox(int value, int x, int y) => new()
    {
        Text = value.ToString(),
        Font = new Font("Segoe UI Variable Display", 9f),
        ForeColor = C_Text,
        FillColor = C_Bg,
        BorderColor = C_Light,
        BorderRadius = 4,
        Size = new Size(60, 28),
        Location = new Point(x, y),
        TextAlign = HorizontalAlignment.Center,
    };

    static Guna2Panel MakeDivider(int x, int y) => new()
    {
        Bounds = new Rectangle(x, y, 316, 1),
        FillColor = C_Border,
        BorderRadius = 0,
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
        _lblStatus.ForeColor = C_Text;
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
    //  Cleanup
    // ═══════════════════════════════════════════════════════════════════════

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _escTimer.Stop();
        _escTimer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
        Environment.Exit(0);
    }
}
