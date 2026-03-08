#![windows_subsystem = "windows"]

use std::ffi::OsStr;
use std::mem;
use std::os::windows::ffi::OsStrExt;
use std::path::PathBuf;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;

use serde::{Deserialize, Serialize};
use winapi::shared::minwindef::*;
use winapi::shared::windef::*;
use winapi::um::commctrl::{InitCommonControlsEx, INITCOMMONCONTROLSEX, ICC_STANDARD_CLASSES};
use winapi::um::libloaderapi::GetModuleHandleW;
use winapi::um::synchapi::Sleep;
use winapi::um::wingdi::*;
use winapi::um::winuser::*;

// ── Config ──────────────────────────────────────────────────────────────────

#[derive(Serialize, Deserialize, Clone)]
struct Config {
    hotkey_vk: u32,
    exit_btn: [i32; 2],
    return_btn: [i32; 2],
    yes_btn: [i32; 2],
    delay_ms: u32,
}

impl Default for Config {
    fn default() -> Self {
        Self {
            hotkey_vk: 0x75, // F6
            exit_btn: [1390, 57],
            return_btn: [1186, 298],
            yes_btn: [1186, 714],
            delay_ms: 500,
        }
    }
}

fn config_path() -> PathBuf {
    let mut path = std::env::current_exe().unwrap_or_default();
    path.pop();
    path.push("fn-leave-config.json");
    path
}

fn load_config() -> Config {
    let path = config_path();
    if let Ok(data) = std::fs::read_to_string(&path) {
        serde_json::from_str(&data).unwrap_or_default()
    } else {
        let cfg = Config::default();
        save_config(&cfg);
        cfg
    }
}

fn save_config(cfg: &Config) {
    let path = config_path();
    if let Ok(data) = serde_json::to_string_pretty(cfg) {
        let _ = std::fs::write(path, data);
    }
}

// ── VK name helper ─────────────────────────────────────────────────────────

fn vk_to_name(vk: u32) -> String {
    match vk {
        0x08 => "Backspace".into(),
        0x09 => "Tab".into(),
        0x0D => "Enter".into(),
        0x1B => "Escape".into(),
        0x20 => "Space".into(),
        0x21 => "PageUp".into(),
        0x22 => "PageDown".into(),
        0x23 => "End".into(),
        0x24 => "Home".into(),
        0x25 => "Left".into(),
        0x26 => "Up".into(),
        0x27 => "Right".into(),
        0x28 => "Down".into(),
        0x2D => "Insert".into(),
        0x2E => "Delete".into(),
        0x13 => "Pause".into(),
        0x30..=0x39 => format!("{}", vk - 0x30),
        0x41..=0x5A => format!("{}", vk as u8 as char),
        0x60..=0x69 => format!("Numpad{}", vk - 0x60),
        0x70..=0x7B => format!("F{}", vk - 0x70 + 1),
        _ => format!("0x{:02X}", vk),
    }
}

// ── Macro (SendInput) ───────────────────────────────────────────────────────

unsafe fn send_key(vk: u16) {
    let mut inputs: [INPUT; 2] = mem::zeroed();

    inputs[0].type_ = INPUT_KEYBOARD;
    let ki_down = inputs[0].u.ki_mut();
    ki_down.wVk = vk;

    inputs[1].type_ = INPUT_KEYBOARD;
    let ki_up = inputs[1].u.ki_mut();
    ki_up.wVk = vk;
    ki_up.dwFlags = KEYEVENTF_KEYUP;

    SendInput(2, inputs.as_mut_ptr(), mem::size_of::<INPUT>() as i32);
}

unsafe fn click_at(x: i32, y: i32) {
    let screen_w = GetSystemMetrics(SM_CXSCREEN);
    let screen_h = GetSystemMetrics(SM_CYSCREEN);
    let abs_x = (x as i64 * 65535 / screen_w as i64) as i32;
    let abs_y = (y as i64 * 65535 / screen_h as i64) as i32;

    let mut inputs: [INPUT; 3] = mem::zeroed();

    inputs[0].type_ = INPUT_MOUSE;
    let mi = inputs[0].u.mi_mut();
    mi.dx = abs_x;
    mi.dy = abs_y;
    mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE;

    inputs[1].type_ = INPUT_MOUSE;
    let mi = inputs[1].u.mi_mut();
    mi.dx = abs_x;
    mi.dy = abs_y;
    mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN;

    inputs[2].type_ = INPUT_MOUSE;
    let mi = inputs[2].u.mi_mut();
    mi.dx = abs_x;
    mi.dy = abs_y;
    mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP;

    SendInput(3, inputs.as_mut_ptr(), mem::size_of::<INPUT>() as i32);
}

// ── Win32 helpers ───────────────────────────────────────────────────────────

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(Some(0)).collect()
}

// ── Control IDs ─────────────────────────────────────────────────────────────

const ID_TOGGLE: i32 = 101;
const ID_SETKEY: i32 = 102;
const HOTKEY_ID: i32 = 1;
const TIMER_KEYCAPTURE: usize = 1;

// ── Window state ────────────────────────────────────────────────────────────

struct AppState {
    config: Config,
    enabled: bool,
    waiting_for_key: bool,
    label_hotkey: HWND,
    label_status: HWND,
    btn_setkey: HWND,
    chk_toggle: HWND,
    font: HFONT,
}

static MACRO_RUNNING: AtomicBool = AtomicBool::new(false);
static mut APP: *mut AppState = ptr::null_mut();

// ── Key capture via polling ─────────────────────────────────────────────────

// VK codes to scan during key capture
const CAPTURE_VKS: &[u32] = &[
    // F1-F12
    0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B,
    // 0-9
    0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
    // A-Z
    0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B, 0x4C,
    0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
    0x59, 0x5A,
    // Numpad 0-9
    0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
    // Navigation
    0x21, 0x22, 0x23, 0x24, 0x2D, 0x2E, // PgUp PgDn End Home Ins Del
    0x13, // Pause
    0x08, 0x09, 0x20, // Backspace Tab Space
];

unsafe fn check_key_capture(hwnd: HWND) {
    if APP.is_null() { return; }
    let app = &mut *APP;
    if !app.waiting_for_key { return; }

    for &vk in CAPTURE_VKS {
        if GetAsyncKeyState(vk as i32) & 1 != 0 {
            app.config.hotkey_vk = vk;
            save_config(&app.config);

            UnregisterHotKey(hwnd, HOTKEY_ID);
            RegisterHotKey(hwnd, HOTKEY_ID, 0, vk);

            let label = format!("Hotkey:  {}", vk_to_name(vk));
            SetWindowTextW(app.label_hotkey, wide(&label).as_ptr());
            SetWindowTextW(app.btn_setkey, wide("Set Key").as_ptr());

            app.waiting_for_key = false;
            KillTimer(hwnd, TIMER_KEYCAPTURE);
            return;
        }
    }
}

// ── Window procedure ────────────────────────────────────────────────────────

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: UINT, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_CREATE => 0,
        WM_TIMER => {
            if wp == TIMER_KEYCAPTURE {
                check_key_capture(hwnd);
            }
            0
        }
        WM_HOTKEY => {
            if !APP.is_null() {
                let app = &mut *APP;
                if app.enabled && wp as i32 == HOTKEY_ID {
                    if !MACRO_RUNNING.swap(true, Ordering::SeqCst) {
                        let cfg = app.config.clone();
                        thread::spawn(move || {
                            let delay = cfg.delay_ms;
                            send_key(VK_ESCAPE as u16);
                            Sleep(delay);
                            click_at(cfg.exit_btn[0], cfg.exit_btn[1]);
                            Sleep(delay);
                            click_at(cfg.return_btn[0], cfg.return_btn[1]);
                            Sleep(delay);
                            click_at(cfg.yes_btn[0], cfg.yes_btn[1]);
                            MACRO_RUNNING.store(false, Ordering::SeqCst);
                        });
                    }
                }
            }
            0
        }
        WM_COMMAND => {
            if !APP.is_null() {
                let app = &mut *APP;
                let id = LOWORD(wp as u32) as i32;
                let notify = HIWORD(wp as u32);

                if id == ID_SETKEY && notify == BN_CLICKED {
                    app.waiting_for_key = true;
                    SetWindowTextW(app.btn_setkey, wide("Press a key...").as_ptr());
                    // Temporarily unregister hotkey so it doesn't fire during capture
                    UnregisterHotKey(hwnd, HOTKEY_ID);
                    // Start polling timer at 50ms
                    SetTimer(hwnd, TIMER_KEYCAPTURE, 50, None);
                } else if id == ID_TOGGLE && notify == BN_CLICKED {
                    let checked = SendMessageW(app.chk_toggle, BM_GETCHECK, 0, 0);
                    app.enabled = checked == BST_CHECKED as isize;
                    let status = if app.enabled { "Status:  ON" } else { "Status:  OFF" };
                    SetWindowTextW(app.label_status, wide(status).as_ptr());
                }
            }
            0
        }
        WM_CTLCOLORSTATIC => {
            let hdc = wp as HDC;
            SetBkMode(hdc, TRANSPARENT as i32);
            SetTextColor(hdc, RGB(220, 220, 220));
            GetStockObject(BLACK_BRUSH as i32) as LRESULT
        }
        WM_ERASEBKGND => {
            let hdc = wp as HDC;
            let mut rc: RECT = mem::zeroed();
            GetClientRect(hwnd, &mut rc);
            let brush = CreateSolidBrush(RGB(30, 30, 30));
            FillRect(hdc, &rc, brush);
            DeleteObject(brush as *mut _);
            1
        }
        WM_DESTROY => {
            if !APP.is_null() {
                let app = &*APP;
                DeleteObject(app.font as *mut _);
            }
            UnregisterHotKey(hwnd, HOTKEY_ID);
            PostQuitMessage(0);
            0
        }
        _ => DefWindowProcW(hwnd, msg, wp, lp),
    }
}

// ── Entry point ─────────────────────────────────────────────────────────────

fn main() {
    let config = load_config();

    unsafe {
        // Enable modern common controls
        let icc = INITCOMMONCONTROLSEX {
            dwSize: mem::size_of::<INITCOMMONCONTROLSEX>() as u32,
            dwICC: ICC_STANDARD_CLASSES,
        };
        InitCommonControlsEx(&icc);

        let hinstance = GetModuleHandleW(ptr::null());
        let class_name = wide("FNLeaveClass");

        let wc = WNDCLASSEXW {
            cbSize: mem::size_of::<WNDCLASSEXW>() as u32,
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: hinstance,
            hIcon: LoadIconW(ptr::null_mut(), IDI_APPLICATION),
            hCursor: LoadCursorW(ptr::null_mut(), IDC_ARROW),
            hbrBackground: ptr::null_mut(),
            lpszMenuName: ptr::null(),
            lpszClassName: class_name.as_ptr(),
            hIconSm: ptr::null_mut(),
        };

        RegisterClassExW(&wc);

        let screen_w = GetSystemMetrics(SM_CXSCREEN);
        let screen_h = GetSystemMetrics(SM_CYSCREEN);
        let win_w = 340;
        let win_h = 190;
        let win_x = (screen_w - win_w) / 2;
        let win_y = (screen_h - win_h) / 2;

        let hwnd = CreateWindowExW(
            0,
            class_name.as_ptr(),
            wide("FN Leave").as_ptr(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            win_x, win_y, win_w, win_h,
            ptr::null_mut(),
            ptr::null_mut(),
            hinstance,
            ptr::null_mut(),
        );

        if hwnd.is_null() {
            return;
        }

        // Try to enable dark title bar (Windows 10 1809+)
        #[allow(non_upper_case_globals)]
        const DWMWA_USE_IMMERSIVE_DARK_MODE: u32 = 20;
        type DwmSetWindowAttributeFn = unsafe extern "system" fn(HWND, u32, *const BOOL, u32) -> i32;
        let dwmapi = wide("dwmapi.dll");
        let dwm_module = winapi::um::libloaderapi::LoadLibraryW(dwmapi.as_ptr());
        if !dwm_module.is_null() {
            let proc_name = b"DwmSetWindowAttribute\0";
            let proc = winapi::um::libloaderapi::GetProcAddress(dwm_module, proc_name.as_ptr() as *const i8);
            if !proc.is_null() {
                let func: DwmSetWindowAttributeFn = mem::transmute(proc);
                let dark: BOOL = 1;
                func(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, mem::size_of::<BOOL>() as u32);
            }
        }

        // Create Segoe UI font
        let font = CreateFontW(
            18, 0, 0, 0,
            FW_NORMAL as i32, 0, 0, 0,
            DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
            DEFAULT_PITCH | FF_DONTCARE,
            wide("Segoe UI").as_ptr(),
        );

        let static_class = wide("STATIC");
        let button_class = wide("BUTTON");

        // ── Hotkey row ──
        let hotkey_text = format!("Hotkey:  {}", vk_to_name(config.hotkey_vk));
        let label_hotkey = CreateWindowExW(
            0, static_class.as_ptr(), wide(&hotkey_text).as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            24, 24, 180, 24,
            hwnd, ptr::null_mut(), hinstance, ptr::null_mut(),
        );
        SendMessageW(label_hotkey, WM_SETFONT, font as WPARAM, 1);

        let btn_setkey = CreateWindowExW(
            0, button_class.as_ptr(), wide("Set Key").as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
            220, 20, 90, 30,
            hwnd, ID_SETKEY as HMENU, hinstance, ptr::null_mut(),
        );
        SendMessageW(btn_setkey, WM_SETFONT, font as WPARAM, 1);

        // ── Toggle row ──
        let chk_toggle = CreateWindowExW(
            0, button_class.as_ptr(), wide("Enabled").as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
            24, 70, 120, 24,
            hwnd, ID_TOGGLE as HMENU, hinstance, ptr::null_mut(),
        );
        SendMessageW(chk_toggle, WM_SETFONT, font as WPARAM, 1);
        SendMessageW(chk_toggle, BM_SETCHECK, BST_CHECKED as usize, 0);

        // ── Status row ──
        let label_status = CreateWindowExW(
            0, static_class.as_ptr(), wide("Status:  ON").as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            24, 116, 280, 24,
            hwnd, ptr::null_mut(), hinstance, ptr::null_mut(),
        );
        SendMessageW(label_status, WM_SETFONT, font as WPARAM, 1);

        // ── App state ──
        let mut app_state = Box::new(AppState {
            config,
            enabled: true,
            waiting_for_key: false,
            label_hotkey,
            label_status,
            btn_setkey,
            chk_toggle,
            font,
        });

        APP = &mut *app_state as *mut AppState;

        RegisterHotKey(hwnd, HOTKEY_ID, 0, app_state.config.hotkey_vk);

        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);

        let mut msg: MSG = mem::zeroed();
        while GetMessageW(&mut msg, ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        APP = ptr::null_mut();
    }
}
