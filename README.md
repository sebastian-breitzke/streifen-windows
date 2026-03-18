# Streifen for Windows

Windows port of [Streifen](https://streifen.app), a horizontal scrolling window manager.

## macOS Reference

Ported from macOS source at commit [`a55f808`](https://github.com/sbreitzke/streifen/commit/a55f808) (2026-03-18).

Key features synced:
- Slice-based grid system (replaced widthRatio)
- HUD overlay with brand colors, light/dark adaptive
- Manual resize snap to slice grid
- State persistence after every structural change
- Removed cached resizability (Rider/JetBrains fix)

## Concept

Windows arranged in a horizontal strip. 9 virtual workspaces. Keyboard-driven.

```
┌──────────┐  ┌──────────────────┐  ┌──────────┐  ┌──────────────┐
│ Terminal  │  │     Browser      │  │  Slack   │  │    Code      │
│ (2 sl)   │  │    (3 sl)        │  │ (2 sl)   │  │   (3 sl)     │
└──────────┘  └──────────────────┘  └──────────┘  └──────────────┘
              ← Hyper+H/L or Arrow Keys →
```

## Slice Grid

Screen divided into fixed slices. Each window occupies 1–N slices.

| Screen Class | Aspect Ratio | Total Slices |
|-------------|-------------|-------------|
| Laptop      | < 1.5       | 4           |
| Desktop     | 1.5 – 2.3  | 6           |
| Ultrawide   | ≥ 2.3      | 8           |

## Hotkeys

**Hyper = Ctrl + Alt + Win**

| Key | Action |
|-----|--------|
| Hyper+1-9 | Switch workspace |
| Hyper+Shift+1-9 | Move window to workspace |
| Hyper+H/L/←/→ | Focus left/right |
| Hyper+Shift+←/→ | Reorder in strip |
| Hyper+↑/↓ | Next/prev workspace |
| Hyper+Shift+↑/↓ | Move window to next/prev workspace |
| Hyper+F1-F8 | Set 1-8 slices |
| Hyper+- / Hyper+= | Slice count ±1 |
| Hyper+Shift+F1-F5 | Set app default (XS/S/M/L/XL) |
| Hyper+Shift+Esc | Reset all sizes |
| Hyper+Shift+F12 | Debug dump |

## Tech

- .NET 8, WPF (system tray + hidden hotkey window)
- Win32 P/Invoke: EnumWindows, SetWinEventHook, SetWindowPos, RegisterHotKey
- Debug server: `http://localhost:22222/state`
- State: `%APPDATA%\Streifen\state.json`
- Logs: `%APPDATA%\Streifen\streifen.log`

## Build

```
dotnet build src/Streifen.Windows/Streifen.Windows.csproj
```

## License

MIT
