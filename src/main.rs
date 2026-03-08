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
use winapi::um::libloaderapi::GetModuleHandleW;
use winapi::um::synchapi::Sleep;
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
        0x30..=0x39 => format!("{}", (vk - 0x30)),
        0x41..=0x5A => format!("{}", (vk - 0x41 + 0x41) as u8 as char),
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

    // Move
    inputs[0].type_ = INPUT_MOUSE;
    let mi_move = inputs[0].u.mi_mut();
    mi_move.dx = abs_x as i32;
    mi_move.dy = abs_y as i32;
    mi_move.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE;

    // Button down
    inputs[1].type_ = INPUT_MOUSE;
    let mi_down = inputs[1].u.mi_mut();
    mi_down.dx = abs_x as i32;
    mi_down.dy = abs_y as i32;
    mi_down.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN;

    // Button up
    inputs[2].type_ = INPUT_MOUSE;
    let mi_up = inputs[2].u.mi_mut();
    mi_up.dx = abs_x as i32;
    mi_up.dy = abs_y as i32;
    mi_up.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP;

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

// ── Window state ────────────────────────────────────────────────────────────

struct AppState {
    config: Config,
    enabled: bool,
    waiting_for_key: bool,
    label_hotkey: HWND,
    label_status: HWND,
    btn_setkey: HWND,
    chk_toggle: HWND,
}

static MACRO_RUNNING: AtomicBool = AtomicBool::new(false);

static mut APP: *mut AppState = ptr::null_mut();

// ── Window procedure ────────────────────────────────────────────────────────

unsafe extern "system" fn wnd_proc(hwnd: HWND, msg: UINT, wp: WPARAM, lp: LPARAM) -> LRESULT {
    match msg {
        WM_CREATE => {
            // Controls will be created after CreateWindowExW returns
            0
        }
        WM_HOTKEY => {
            if !APP.is_null() {
                let app = &mut *APP;
                if app.enabled && wp as i32 == HOTKEY_ID {
                    if !MACRO_RUNNING.swap(true, Ordering::SeqCst) {
                        let cfg = app.config.clone();
                        thread::spawn(move || {
                            // Run macro
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
                } else if id == ID_TOGGLE && notify == BN_CLICKED {
                    let checked = SendMessageW(app.chk_toggle, BM_GETCHECK, 0, 0);
                    app.enabled = checked == BST_CHECKED as isize;
                    let status = if app.enabled { "Status: ON" } else { "Status: OFF" };
                    SetWindowTextW(app.label_status, wide(status).as_ptr());
                }
            }
            0
        }
        WM_KEYDOWN => {
            if !APP.is_null() {
                let app = &mut *APP;
                if app.waiting_for_key {
                    let new_vk = wp as u32;
                    app.config.hotkey_vk = new_vk;
                    save_config(&app.config);

                    // Re-register hotkey
                    UnregisterHotKey(hwnd, HOTKEY_ID);
                    RegisterHotKey(hwnd, HOTKEY_ID, 0, new_vk);

                    // Update labels
                    let name = vk_to_name(new_vk);
                    let label = format!("Hotkey: {}", name);
                    SetWindowTextW(app.label_hotkey, wide(&label).as_ptr());
                    SetWindowTextW(app.btn_setkey, wide("Set Key").as_ptr());

                    app.waiting_for_key = false;
                    return 0;
                }
            }
            DefWindowProcW(hwnd, msg, wp, lp)
        }
        WM_DESTROY => {
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
            hbrBackground: (COLOR_BTNFACE + 1) as HBRUSH,
            lpszMenuName: ptr::null(),
            lpszClassName: class_name.as_ptr(),
            hIconSm: ptr::null_mut(),
        };

        RegisterClassExW(&wc);

        // Center the window on screen
        let screen_w = GetSystemMetrics(SM_CXSCREEN);
        let screen_h = GetSystemMetrics(SM_CYSCREEN);
        let win_w = 340;
        let win_h = 200;
        let win_x = (screen_w - win_w) / 2;
        let win_y = (screen_h - win_h) / 2;

        let hwnd = CreateWindowExW(
            0,
            class_name.as_ptr(),
            wide("FN Leave").as_ptr(),
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
            win_x,
            win_y,
            win_w,
            win_h,
            ptr::null_mut(),
            ptr::null_mut(),
            hinstance,
            ptr::null_mut(),
        );

        if hwnd.is_null() {
            return;
        }

        let static_class = wide("STATIC");
        let button_class = wide("BUTTON");

        // Hotkey label
        let hotkey_text = format!("Hotkey: {}", vk_to_name(config.hotkey_vk));
        let label_hotkey = CreateWindowExW(
            0,
            static_class.as_ptr(),
            wide(&hotkey_text).as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            20, 20, 180, 25,
            hwnd,
            ptr::null_mut(),
            hinstance,
            ptr::null_mut(),
        );

        // Set Key button
        let btn_setkey = CreateWindowExW(
            0,
            button_class.as_ptr(),
            wide("Set Key").as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
            210, 17, 100, 30,
            hwnd,
            ID_SETKEY as HMENU,
            hinstance,
            ptr::null_mut(),
        );

        // Enabled checkbox
        let chk_toggle = CreateWindowExW(
            0,
            button_class.as_ptr(),
            wide("Enabled").as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_AUTOCHECKBOX,
            20, 65, 120, 25,
            hwnd,
            ID_TOGGLE as HMENU,
            hinstance,
            ptr::null_mut(),
        );

        // Check it by default
        SendMessageW(chk_toggle, BM_SETCHECK, BST_CHECKED as usize, 0);

        // Status label
        let label_status = CreateWindowExW(
            0,
            static_class.as_ptr(),
            wide("Status: ON").as_ptr(),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            20, 110, 280, 25,
            hwnd,
            ptr::null_mut(),
            hinstance,
            ptr::null_mut(),
        );

        // Set up app state
        let mut app_state = Box::new(AppState {
            config,
            enabled: true,
            waiting_for_key: false,
            label_hotkey,
            label_status,
            btn_setkey,
            chk_toggle,
        });

        APP = &mut *app_state as *mut AppState;

        // Register global hotkey
        RegisterHotKey(hwnd, HOTKEY_ID, 0, app_state.config.hotkey_vk);

        ShowWindow(hwnd, SW_SHOW);
        UpdateWindow(hwnd);

        // Message loop
        let mut msg: MSG = mem::zeroed();
        while GetMessageW(&mut msg, ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        APP = ptr::null_mut();
        // app_state drops here
    }
}
