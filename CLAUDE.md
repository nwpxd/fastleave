# FastLeave

Fortnite leave-match macro for Windows. Press a hotkey (default F6) to automatically leave a match via the in-game menu.

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

## Macro Sequence

The macro performs 4 steps in Fortnite's side panel:

1. **Escape** — opens the side panel menu
2. **Click exit icon** — top-right door icon at (1832, 76)
3. **Click "Return to lobby"** — button at (1570, 384)
4. **Click "Yes"** — confirmation at (1574, 922)

The macro runs **twice** automatically to handle cases where the first Escape closes a sub-state (build mode, inventory, map) instead of opening the leave menu.

## Key Technical Decisions

- **SendInput with MOUSEEVENTF_ABSOLUTE** for mouse movement — SetCursorPos doesn't register in Fortnite's input system
- **GetAsyncKeyState polling** for hotkey capture — WM_KEYDOWN doesn't work when a button has focus
- **User interrupt detection** — checks WASD, arrows, mouse buttons, Escape, Space every 15ms during waits. If user touches any input, macro aborts immediately
- **FocusFortnite via EnumWindows** — finds UnrealWindow class with "Fortnite" in title, calls SetForegroundWindow before macro
- **Environment.Exit(0)** in OnFormClosing — prevents background thread from keeping process alive
- **highestAvailable** manifest (not requireAdministrator) — requireAdministrator blocks silent launch

## Config Defaults (fastleave.json)

```json
{
  "HotkeyVk": 117,        // F6
  "ExitBtn": [1832, 76],
  "ReturnBtn": [1570, 384],
  "YesBtn": [1574, 922],
  "EscapeDelayMs": 700,
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
2. `MainForm.cs` the `_lblVer` label: `Text = "vX.Y.Z"`

Version scheme: `0.X.Y` during development, `1.0.0` for stable release.

- Bump **patch** (0.1.0 → 0.1.1) for small fixes, timing tweaks, coordinate changes
- Bump **minor** (0.1.0 → 0.2.0) for new features, UI changes, logic rewrites
- Current version: **0.6.0** (active development)

## Known Issues & Fixes History

- Mouse teleport (SetCursorPos) not registering in game → use SendInput absolute
- First Escape may close sub-state instead of opening menu → run macro sequence twice
- App not closing (file locked) → Environment.Exit(0) on close
- Self-contained exe "dll not found" → file was corrupted during copy, re-publish
- DPI scaling wrong coordinates → GetSystemMetrics with DPI-aware app + PerMonitorV2

## Reference Screenshots

`1.png` — Side panel after Escape (exit door icon circled, top-right)
`2.png` — Exit page after clicking door icon ("Return to lobby" circled)
`3.png` — Confirmation dialog ("Yes" button circled)
