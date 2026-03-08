# FastLeave

Fortnite leave-match macro for Windows. Press Escape in-game — the macro detects it and automatically clicks through the leave menu.

## Tech Stack

- **Language**: C# / WinForms (.NET 10)
- **Target**: Windows x64, self-contained single-file exe (no runtime needed)
- **Resolution**: 1920x1080 hardcoded click coordinates

## Project Structure

```
FastLeave.csproj   — Project config, version, publish settings
Program.cs         — Entry point, single-instance Mutex guard
MainForm.cs        — ALL app logic (UI, macro, config, Win32 interop)
app.manifest       — Admin elevation (highestAvailable)
icon.ico           — App icon (door + arrow + running figure)
fastleave.json     — Runtime config (gitignored, created on first run)
```

## How It Works

The user presses Escape in Fortnite (which opens the side panel). The macro detects the Escape keypress via `GetAsyncKeyState` polling (30ms interval) and automatically clicks through 3 buttons:

1. **Click exit icon** — top-right door icon at (1832, 76)
2. **Click "Return to lobby"** — button at (1570, 384)
3. **Click "Yes"** — confirmation at (1574, 922)

No hotkey registration needed — the macro piggybacks on the user's natural Escape press. Toggle on/off from the UI.

## Key Technical Decisions

- **GetAsyncKeyState polling (30ms)** — detects Escape without blocking it; key still reaches Fortnite
- **SendInput with MOUSEEVENTF_ABSOLUTE** for mouse clicks — reliable with game UIs
- **FocusFortnite via EnumWindows** — finds UnrealWindow class, calls SetForegroundWindow before clicking
- **Environment.Exit(0)** in OnFormClosing — prevents background thread from keeping process alive
- **highestAvailable** manifest (not requireAdministrator) — requireAdministrator blocks silent launch

## Config Defaults (fastleave.json)

```json
{
  "ExitBtn": [1832, 76],
  "ReturnBtn": [1570, 384],
  "YesBtn": [1574, 922],
  "EscapeDelayMs": 800,
  "ClickDelayMs": 300,
  "MinimizeToTray": true
}
```

## Build & Publish

```bash
dotnet publish -c Release -o publish
```

Output: `publish/fastleave.exe` (~50MB self-contained single file)

## Versioning

**Every time code is modified and a new build is produced, the version MUST be bumped.**

Update in **both** places:

1. `FastLeave.csproj` line `<Version>X.Y.Z</Version>`
2. `MainForm.cs` the `lblVer` label: `Text = "vX.Y.Z"`

Version scheme: `0.X.Y` during development, `1.0.0` for stable release.

- Bump **patch** (1.0.0 → 1.0.1) for small fixes, timing tweaks, coordinate changes
- Bump **minor** (1.0.0 → 1.1.0) for new features, UI changes, logic rewrites
- Current version: **1.3.0**

## Known Issues & Fixes History

- Mouse teleport (SetCursorPos) not registering in game → use SendInput absolute
- App not closing (file locked) → Environment.Exit(0) on close
- Self-contained exe "dll not found" → file was corrupted during copy, re-publish
- DPI scaling wrong coordinates → GetSystemMetrics with DPI-aware app + PerMonitorV2
- BlockInput blocks mouse_event/SetCursorPos (only SendInput bypasses it) → use SendInput for everything
- RegisterHotKey swallows keypress → switched to GetAsyncKeyState polling

## Reference Screenshots

`1.png` — Side panel after Escape (exit door icon circled, top-right)
`2.png` — Exit page after clicking door icon ("Return to lobby" circled)
`3.png` — Confirmation dialog ("Yes" button circled)
