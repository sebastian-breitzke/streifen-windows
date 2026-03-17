# Streifen for Windows — .NET Port

## Context

Streifen is a scrolling window manager for macOS, written in Swift 6. This project is a Windows port using .NET (C#, WPF for system tray). Same concept: horizontal strip layout, virtual workspaces, T-shirt sizing, keyboard-driven navigation.

The macOS source lives at `/Users/sb/projects/streifen/` — use it as architectural reference.

---

## Core Concept

Windows are arranged horizontally in a scrollable strip. 9 virtual workspaces, fast switching via Hyper keys. No tree, no nesting — flat strip per workspace.

```
[ Workspace 1 ]
┌──────────┐  ┌──────────────────┐  ┌──────────┐  ┌──────────────┐
│ Terminal  │  │     Browser      │  │  Slack   │  │    Code      │
│   (S)    │  │      (L)         │  │   (M)    │  │    (L)       │
└──────────┘  └──────────────────┘  └──────────┘  └──────────────┘
              ← Hyper+H/L or Arrow Keys →
```

---

## Architecture (Port from macOS)

```
Streifen.Windows (System Tray App)
├── WindowTracker        — Win32 window discovery + WinEventHook monitoring
├── WorkspaceManager     — 9 workspaces, off-screen hiding, state persistence
├── StripLayout          — Horizontal layout with gaps + peek margins
├── HotkeyManager        — Global hotkeys via RegisterHotKey
├── DebugServer          — HTTP localhost:22222, JSON state API
├── TrayIcon             — WPF NotifyIcon, workspace indicator
└── StreifenConfig       — AppSize, ScreenClass, pinned/follow apps
```

### Layer Stack

```
┌─────────────────────────────────────┐
│  Hotkey Input (HotkeyManager)       │  RegisterHotKey → command dispatch
├─────────────────────────────────────┤
│  Workspace State (WorkspaceManager) │  9 virtual spaces, focus tracking,
│                                     │  scroll offset per workspace
├─────────────────────────────────────┤
│  Window Tracking (WindowTracker)    │  WinEventHook, EnumWindows,
│                                     │  lifecycle monitoring, filtering
├─────────────────────────────────────┤
│  Layout Engine (StripLayout)        │  Horizontal strip calculation,
│                                     │  width ratios, peek, off-screen park
└─────────────────────────────────────┘
```

---

## Key Data Structures

### TrackedWindow

```csharp
class TrackedWindow
{
    public IntPtr Hwnd;              // Win32 window handle
    public string ProcessName;       // e.g. "chrome", "code"
    public string ClassName;         // Win32 window class
    public string Title;             // Window title
    public RECT Frame;               // Current position/size
    public double VirtualX;          // Logical position in strip
    public double WidthRatio;        // Ratio of screen width (0.20–1.00)
    public AppSize Size;             // T-shirt size
    public bool Resizable;           // Cached resizability
}
```

### Workspace

```csharp
class Workspace
{
    public int Id;                          // 1–9
    public List<TrackedWindow> Windows;     // Ordered strip (left to right)
    public double ScrollOffset;             // Scroll position (≤ 0)
    public int FocusIndex;                  // Focused window index
    public bool IsVisible;                  // Is this workspace on-screen?
}
```

### AppSize (T-Shirt Sizes)

```csharp
enum AppSize { XS, S, M, L, XL, Full }

// Width ratios by screen class:
//        Laptop  Desktop  Ultrawide
// XS     0.33    0.20     0.20
// S      0.50    0.25     0.20
// M      1.00    0.33     0.25
// L      1.00    0.50     0.33
// XL     1.00    0.67     0.50
// Full   1.00    1.00     1.00
```

### ScreenClass

```csharp
enum ScreenClass { Laptop, Desktop, Ultrawide }

// Detection: aspect ratio of primary monitor
// Laptop:    < 1.5
// Desktop:   1.5 – 2.3
// Ultrawide: ≥ 2.3
```

---

## Component Details

### 1. WindowTracker

**Win32 APIs needed:**
- `EnumWindows` — initial window discovery
- `SetWinEventHook` — monitor window lifecycle events:
  - `EVENT_OBJECT_CREATE` → new window
  - `EVENT_OBJECT_DESTROY` → window closed
  - `EVENT_OBJECT_LOCATIONCHANGE` → moved/resized
  - `EVENT_SYSTEM_FOREGROUND` → focus changed
  - `EVENT_SYSTEM_MOVESIZEEND` → user finished dragging
- `GetWindowText` — window title
- `GetClassName` — window class
- `GetWindowRect` — current frame
- `IsWindowVisible` — visibility check
- `GetWindowLong(GWL_STYLE)` — check WS_OVERLAPPEDWINDOW for real windows
- `GetWindowThreadProcessId` → Process.GetProcessById() → ProcessName

**Window Filter (skip these):**
- Not visible (`!IsWindowVisible`)
- No title (empty `GetWindowText`)
- Tool windows (`WS_EX_TOOLWINDOW` style)
- Child windows (`WS_CHILD` style)
- Width < 100 or Height < 100
- Cloaked windows (`DwmGetWindowAttribute(DWMWA_CLOAKED)`)
- UWP splash screens, system tray popups
- Own process windows (Streifen itself)

**Liveness check:** Timer every 2s, verify `IsWindow(hwnd)` for all tracked windows.

**Programmatic update flag:** When layout moves windows, set `IsUpdating = true` to ignore self-generated events.

### 2. WorkspaceManager

**9 workspaces**, only one visible at a time.

**Workspace switching:**
1. Hide current workspace: move all windows to off-screen park `(-32000, -32000)` via `SetWindowPos`
2. Set `activeWorkspaceId` to target
3. Show target: layout all windows via StripLayout
4. Raise and focus the workspace's focused window

**Off-screen park:** `(-32000, -32000)` — Win32 convention for hidden windows. Alternatively use `ShowWindow(SW_HIDE)` / `ShowWindow(SW_SHOW)`, but off-screen positioning avoids taskbar flash.

**Window assignment:**
- New windows → active workspace (default)
- Pinned apps → first window goes to configured workspace
- Follow apps → pulled to current workspace on focus

**Focus handling:**
- Track `focusIndex` per workspace
- On external focus change (user clicks): find which workspace has the window, optionally auto-switch
- Follow apps: move window to current workspace instead of switching

### 3. StripLayout

**Algorithm (same as macOS):**

```
For each window in workspace.windows:
    1. targetWidth = widthRatio × screenWidth - 2 × gap
    2. If neighbors exist: cap width to screenWidth - 2×gap - 2×peekWidth
    3. Clamp minimum: max(width, 200)
    4. Store virtualX (cumulative logical position)
    5. Visual position = virtualX + scrollOffset
       - If fully off-screen: park at (-32000, -32000)
       - Else: SetWindowPos(x, gap, width, screenHeight - 2×gap)
    6. Advance x += width + gap
```

**Constants:**
- `gap`: 10px between windows
- `peekWidth`: 60px neighbor peek at screen edges

**Scroll offset clamping:**
```
totalWidth = sum of all widths + gaps
if totalWidth + scrollOffset < screenWidth: scrollOffset = screenWidth - totalWidth
if scrollOffset > 0: scrollOffset = 0
```

**ensureWindowVisible(index):**
- Scroll strip so focused window is fully visible
- Reserve peek margins for neighbors
- 5px tolerance to avoid micro-scrolls

### 4. HotkeyManager

**Use `RegisterHotKey` Win32 API** — cleaner than low-level hooks for fixed bindings.

**Modifier: Hyper = Ctrl+Alt+Win** (Windows equivalent of macOS Ctrl+Alt+Cmd)

| Binding | Action |
|---------|--------|
| Hyper+1-9 | Switch to workspace N |
| Hyper+Shift+1-9 | Move focused window to workspace N |
| Hyper+H / Left | Focus left in strip |
| Hyper+L / Right | Focus right in strip |
| Hyper+Shift+Left | Reorder: move window left |
| Hyper+Shift+Right | Reorder: move window right |
| Hyper+Up | Next workspace |
| Hyper+Down | Previous workspace |
| Hyper+Shift+Up | Move window to next workspace |
| Hyper+Shift+Down | Move window to previous workspace |
| Hyper+F1 | Size = Full |
| Hyper+F2 | Size = XL |
| Hyper+F3 | Size = L |
| Hyper+F4 | Size = M |
| Hyper+F5 | Size = S |
| Hyper+Shift+F1-F5 | Set app default size |
| Hyper+Shift+Escape | Reset all sizes to app defaults |

**Implementation:** Hidden WPF window to receive `WM_HOTKEY` messages. Register all hotkeys at startup, unregister on shutdown.

### 5. DebugServer

**HTTP server on `localhost:22222`** using `HttpListener` or Kestrel minimal API.

| Endpoint | Response |
|----------|----------|
| `GET /state` | Full state: all workspaces, windows, config, screen |
| `GET /active` | Active workspace |
| `GET /windows` | Flat list of all windows |
| `GET /workspace/{1-9}` | Single workspace |

JSON response per window:
```json
{
  "hwnd": "0x001A0B2C",
  "process": "chrome",
  "title": "GitHub",
  "appSize": "l",
  "widthRatio": 0.333,
  "resizable": true,
  "frame": { "x": 100, "y": 50, "width": 640, "height": 720 },
  "virtualX": 100,
  "workspace": 1,
  "index": 0,
  "focused": true,
  "offscreen": false
}
```

### 6. TrayIcon (System Tray)

**WPF NotifyIcon** in system tray showing workspace number.

- Dynamic icon: render workspace number (1-9) into bitmap
- Context menu: workspace list, reset, quit
- Double-click: show/hide debug info

### 7. StreifenConfig

```csharp
class StreifenConfig
{
    public double Gap = 10;
    public double PeekWidth = 60;
    public Dictionary<string, int> PinnedApps;      // processName → workspace
    public HashSet<string> FollowApps;               // processNames
    public Dictionary<string, AppSize> AppSizes;     // processName → default size
    public AppSize DefaultSize = AppSize.L;
}
```

**Default app mappings (Windows process names):**
- Terminals (WindowsTerminal, powershell, cmd, wezterm) → S
- Browsers (chrome, msedge, firefox) → L
- IDEs (devenv, Code, rider64, idea64) → L
- Communication (Teams, Outlook, slack) → M
- Small tools (calc) → XS
- Utilities (explorer) → S
- Unknown → L

---

## State Persistence

**File:** `%APPDATA%\Streifen\state.json`

**Saved:**
```json
{
  "activeWorkspace": 1,
  "scrollOffsets": { "1": -50.0, "2": 0.0 },
  "timestamp": "2026-03-17T14:30:00Z",
  "windows": [
    {
      "hwnd": "0x001A0B2C",
      "workspace": 2,
      "widthRatio": 0.5,
      "appSize": "l",
      "processName": "chrome",
      "title": "GitHub"
    }
  ]
}
```

**Restore logic (only if ≤ 15 min old):**
1. Try match by (processName, title) pair
2. Unmatched windows → active workspace
3. Restore scroll offsets and width ratios

**On shutdown:** Move all windows back on-screen to prevent "all windows hidden" state.

---

## Project Structure

```
Streifen.Windows/
├── Streifen.Windows.sln
├── src/
│   └── Streifen.Windows/
│       ├── Streifen.Windows.csproj      (.NET 8, WPF, Windows)
│       ├── Program.cs                    Entry point, single instance check
│       ├── App.xaml / App.xaml.cs        WPF application, tray setup
│       ├── Core/
│       │   ├── TrackedWindow.cs
│       │   ├── Workspace.cs
│       │   ├── AppSize.cs
│       │   ├── ScreenClass.cs
│       │   └── StreifenConfig.cs
│       ├── Services/
│       │   ├── WindowTracker.cs          Win32 window discovery + events
│       │   ├── WorkspaceManager.cs       9 workspaces, state engine
│       │   ├── StripLayout.cs            Horizontal layout calculation
│       │   ├── HotkeyManager.cs          RegisterHotKey dispatch
│       │   ├── DebugServer.cs            HTTP localhost:22222
│       │   └── StateManager.cs           JSON persistence
│       ├── Win32/
│       │   ├── NativeMethods.cs          P/Invoke declarations
│       │   ├── WindowStyles.cs           WS_*, WS_EX_* constants
│       │   └── WinEventHook.cs           SetWinEventHook wrapper
│       ├── UI/
│       │   ├── TrayIcon.cs               System tray icon + menu
│       │   └── HotkeyWindow.xaml/.cs     Hidden window for WM_HOTKEY
│       └── Resources/
│           └── tray-icon.ico
└── README.md
```

---

## .NET Project Setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <ApplicationIcon>Resources\tray-icon.ico</ApplicationIcon>
    <RootNamespace>Streifen.Windows</RootNamespace>
  </PropertyGroup>
</Project>
```

---

## Implementation Order

### Phase 1: Skeleton + Window Tracking
1. Create .NET 8 WPF project with system tray icon
2. Implement Win32 P/Invoke layer (NativeMethods, window styles)
3. WindowTracker: EnumWindows discovery, window filtering
4. WindowTracker: WinEventHook for create/destroy/focus/move
5. TrackedWindow data structure
6. Basic logging to `%APPDATA%\Streifen\streifen.log`

### Phase 2: Workspaces + Hotkeys
1. Workspace + WorkspaceManager (9 workspaces, active switching)
2. Off-screen parking via SetWindowPos(-32000, -32000)
3. HotkeyManager: RegisterHotKey for Hyper+1-9 (workspace switch)
4. Window assignment: new windows to active workspace
5. Pinned apps: first window to target workspace

### Phase 3: Strip Layout
1. StripLayout: horizontal positioning with gaps
2. Peek margins for neighbor windows
3. Scroll offset + clamping
4. ensureWindowVisible for focus changes
5. Hotkeys: focus left/right (Hyper+H/L/arrows)

### Phase 4: T-Shirt Sizes
1. AppSize enum + ScreenClass detection
2. Width ratio calculation per screen class
3. App default sizes by process name
4. Hotkeys: Hyper+F1-F5 for window size, Hyper+Shift+F1-F5 for app defaults

### Phase 5: Polish
1. State persistence (save/restore JSON)
2. Debug HTTP server (localhost:22222)
3. Follow apps behavior
4. Window reordering (Hyper+Shift+arrows)
5. Crash safety: restore windows on-screen at shutdown
6. Tray icon: dynamic workspace number rendering

---

## Win32 API Quick Reference

```csharp
// Window enumeration
[DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
[DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
[DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
[DllImport("user32.dll")] static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
[DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
[DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
[DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
[DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);

// Window positioning
[DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
[DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);

// Event hooks
[DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
[DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

// Hotkeys
[DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
[DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

// DWM (cloaked window check)
[DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
```

---

## macOS → Windows Mapping

| macOS (Streifen) | Windows (.NET) |
|-------------------|----------------|
| AXSwift / AXUIElement | Win32 P/Invoke (user32.dll) |
| NSRunningApplication | Process.GetProcessById() |
| CGWindowID | IntPtr (HWND) |
| AX setAttribute(.position) | SetWindowPos |
| AX performAction(.raise) | BringWindowToTop + SetForegroundWindow |
| NSEvent global monitor | RegisterHotKey + WM_HOTKEY |
| AXObserver notifications | SetWinEventHook |
| bundleId (com.google.Chrome) | Process name ("chrome") |
| NSScreen.main.visibleFrame | Screen.PrimaryScreen.WorkingArea |
| Off-screen (99999, 99999) | Off-screen (-32000, -32000) |
| ~/Library/Application Support | %APPDATA%\Streifen |
| ~/Library/Logs | %APPDATA%\Streifen\Logs |
| MenuBarExtra (SwiftUI) | NotifyIcon (WPF) |
| Ctrl+Alt+Cmd (Hyper) | Ctrl+Alt+Win (Hyper) |

---

## Reference

The macOS source is at `/Users/sb/projects/streifen/Sources/Streifen/`. Key files:
- `WindowTracker.swift` — AX-based window discovery, filtering, observer setup
- `WorkspaceManager.swift` — workspace state engine (754 lines, core logic)
- `StripLayout.swift` — layout algorithm
- `HotkeyManager.swift` — hotkey dispatch table
- `TrackedWindow.swift` — window data structure
- `StreifenConfig.swift` — config structures, T-shirt size ratios
- `DebugServer.swift` — HTTP JSON API
- `AppDelegate.swift` — component wiring and lifecycle
