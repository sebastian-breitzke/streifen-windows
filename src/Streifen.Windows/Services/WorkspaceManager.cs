using System.Diagnostics;
using Streifen.Windows.Core;
using Streifen.Windows.Win32;

namespace Streifen.Windows.Services;

/// <summary>
/// Manages 9 virtual workspaces. Core state engine.
/// Handles window assignment, workspace switching, focus tracking, manual resize snap.
/// </summary>
public sealed class WorkspaceManager
{
    private readonly StreifenConfig _config;
    private readonly Workspace[] _workspaces;
    private int _activeWorkspaceId = 1;
    private WindowTracker? _windowTracker;
    private long _lastLayoutTicks;

    /// <summary>Fired when active workspace changes — triggers layout.</summary>
    public event Action? ActiveWorkspaceChanged;

    /// <summary>Fired when workspace content changes — triggers layout.</summary>
    public event Action? WorkspaceContentChanged;

    /// <summary>Fired after any structural change — triggers state save.</summary>
    public event Action? StateChanged;

    public WorkspaceManager(StreifenConfig config)
    {
        _config = config;
        _workspaces = new Workspace[9];
        for (int i = 0; i < 9; i++)
            _workspaces[i] = new Workspace(i + 1);

        _workspaces[0].IsVisible = true;
    }

    public int ActiveWorkspaceId => _activeWorkspaceId;
    public Workspace ActiveWorkspace => _workspaces[_activeWorkspaceId - 1];
    public Workspace GetWorkspace(int id) => _workspaces[id - 1];
    public IReadOnlyList<Workspace> AllWorkspaces => _workspaces;

    public void SetWindowTracker(WindowTracker tracker)
    {
        _windowTracker = tracker;
    }

    /// <summary>Record that a layout just happened — used for resize cooldown.</summary>
    public void NotifyLayoutPerformed() => _lastLayoutTicks = Stopwatch.GetTimestamp();

    /// <summary>
    /// Initial sort of discovered windows into workspaces.
    /// Pinned apps get their first window placed in configured workspace.
    /// </summary>
    public void InitialSort(IReadOnlyList<TrackedWindow> windows)
    {
        var pinnedPlaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var window in windows)
        {
            // Floating apps — not managed by workspaces
            if (_config.FloatingApps.Contains(window.ProcessName)) continue;

            int targetWorkspace = 1;

            if (_config.PinnedApps.TryGetValue(window.ProcessName, out int pinnedWs) &&
                !pinnedPlaced.Contains(window.ProcessName))
            {
                targetWorkspace = pinnedWs;
                pinnedPlaced.Add(window.ProcessName);
            }

            var ws = GetWorkspace(targetWorkspace);
            ws.Windows.Add(window);
        }

        foreach (var ws in _workspaces)
        {
            if (ws.Windows.Count > 0)
                ws.FocusIndex = 0;
        }

        Log($"Initial sort: {windows.Count} windows across workspaces");
        StateChanged?.Invoke();
    }

    // ---- Window tracker event handlers ----

    public void HandleWindowAdded(TrackedWindow window)
    {
        // Floating apps — not managed
        if (_config.FloatingApps.Contains(window.ProcessName)) return;

        int targetWorkspace = _activeWorkspaceId;

        if (_config.PinnedApps.TryGetValue(window.ProcessName, out int pinnedWs))
        {
            bool hasExisting = _workspaces.Any(ws =>
                ws.Windows.Any(w => w.ProcessName.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase)));

            if (!hasExisting)
                targetWorkspace = pinnedWs;
        }

        var workspace = GetWorkspace(targetWorkspace);
        workspace.InsertAfterFocus(window);

        Log($"Added {window} to workspace {targetWorkspace}");
        WorkspaceContentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void HandleWindowRemoved(IntPtr hwnd)
    {
        foreach (var ws in _workspaces)
        {
            if (ws.Remove(hwnd))
            {
                Log($"Removed {hwnd} from workspace {ws.Id}");
                if (ws.Id == _activeWorkspaceId)
                    WorkspaceContentChanged?.Invoke();
                StateChanged?.Invoke();
                return;
            }
        }
    }

    public void HandleWindowActivated(IntPtr hwnd)
    {
        HandleWindowFocused(hwnd, allowWorkspaceSwitch: true);
    }

    public void HandleWindowFocusedInternal(IntPtr hwnd)
    {
        HandleWindowFocused(hwnd, allowWorkspaceSwitch: false);
    }

    private void HandleWindowFocused(IntPtr hwnd, bool allowWorkspaceSwitch)
    {
        Workspace? sourceWs = null;
        int sourceIdx = -1;

        foreach (var ws in _workspaces)
        {
            var idx = ws.IndexOf(hwnd);
            if (idx >= 0)
            {
                sourceWs = ws;
                sourceIdx = idx;
                break;
            }
        }

        if (sourceWs == null) return;

        var window = sourceWs.Windows[sourceIdx];

        // Same workspace: just update focus index
        if (sourceWs.Id == _activeWorkspaceId)
        {
            sourceWs.FocusIndex = sourceIdx;
            WorkspaceContentChanged?.Invoke();
            return;
        }

        // Follow app: always pull to current workspace, even from internal focus events
        if (_config.FollowApps.Contains(window.ProcessName) &&
            sourceWs.Id != _activeWorkspaceId)
        {
            sourceWs.Remove(hwnd);
            var activeWs = ActiveWorkspace;
            activeWs.InsertAfterFocus(window);
            Log($"Follow: moved {window.ProcessName} to workspace {_activeWorkspaceId}");
            WorkspaceContentChanged?.Invoke();
            StateChanged?.Invoke();
            return;
        }

        // Don't switch workspaces unless explicitly allowed (Alt+Tab, taskbar click).
        // Internal focus events are too noisy — browser popups, autocomplete etc.
        if (!allowWorkspaceSwitch) return;

        // Stale focus: prefer local window of same app
        var localWindow = ActiveWorkspace.Windows
            .FirstOrDefault(w => w.ProcessName.Equals(window.ProcessName, StringComparison.OrdinalIgnoreCase));

        if (localWindow != null)
        {
            ActiveWorkspace.FocusIndex = ActiveWorkspace.IndexOf(localWindow.Hwnd);
            WorkspaceContentChanged?.Invoke();
            return;
        }

        SwitchToWorkspace(sourceWs.Id);
        sourceWs.FocusIndex = sourceIdx;
    }

    // ---- Manual resize snap ----

    /// <summary>
    /// Handle user manual resize: snap width to nearest slice boundary.
    /// Called from WindowTracker on EVENT_SYSTEM_MOVESIZEEND when width changed.
    /// </summary>
    public void HandleManualResize(IntPtr hwnd)
    {
        // Ignore resizes triggered by our own layout (cooldown 500ms)
        if (Stopwatch.GetTimestamp() - _lastLayoutTicks < Stopwatch.Frequency / 2) return;

        var (ws, idx) = FindWindow(hwnd);
        if (ws == null) return;

        var window = ws.Windows[idx];
        var (screenClass, screen) = ScreenClassDetector.Detect();
        int totalSlices = screenClass.TotalSlices();

        double sliceWidth = (double)screen.WorkingArea.Width / totalSlices;
        int newSlices = (int)Math.Round(window.Frame.Width / sliceWidth);
        newSlices = Math.Clamp(newSlices, 1, totalSlices);

        if (newSlices != window.SliceCount)
        {
            window.SliceCount = newSlices;
            Log($"Manual resize snap: {window.ProcessName} → {newSlices} slices");

            if (ws.Id == _activeWorkspaceId)
                WorkspaceContentChanged?.Invoke();

            StateChanged?.Invoke();
        }
    }

    // ---- Workspace switching ----

    public void SwitchToWorkspace(int id)
    {
        if (id < 1 || id > 9 || id == _activeWorkspaceId)
            return;

        Log($"Switch: workspace {_activeWorkspaceId} → {id}");

        var current = ActiveWorkspace;
        current.IsVisible = false;
        ParkWorkspaceOffScreen(current);

        _activeWorkspaceId = id;
        var target = ActiveWorkspace;
        target.IsVisible = true;

        ActiveWorkspaceChanged?.Invoke();
        RaiseFloatingWindows();
        ScheduleOffscreenSweep();
        StateChanged?.Invoke();
    }

    public void MoveWindowToWorkspace(int targetId)
    {
        if (targetId < 1 || targetId > 9 || targetId == _activeWorkspaceId)
            return;

        var ws = ActiveWorkspace;
        var window = ws.FocusedWindow;
        if (window == null) return;

        ws.Remove(window.Hwnd);

        var targetWs = GetWorkspace(targetId);
        targetWs.InsertAfterFocus(window);

        NativeMethods.SetWindowPos(window.Hwnd, IntPtr.Zero, -32000, -32000, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        Log($"Moved {window.ProcessName} to workspace {targetId}");
        WorkspaceContentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    // ---- Focus navigation ----

    public void FocusLeft()
    {
        var ws = ActiveWorkspace;
        if (ws.Windows.Count == 0) return;
        ws.FocusIndex = Math.Max(0, ws.FocusIndex - 1);
        WorkspaceContentChanged?.Invoke();
    }

    public void FocusRight()
    {
        var ws = ActiveWorkspace;
        if (ws.Windows.Count == 0) return;
        ws.FocusIndex = Math.Min(ws.Windows.Count - 1, ws.FocusIndex + 1);
        WorkspaceContentChanged?.Invoke();
    }

    public void ReorderLeft()
    {
        var ws = ActiveWorkspace;
        if (ws.FocusIndex <= 0) return;
        (ws.Windows[ws.FocusIndex], ws.Windows[ws.FocusIndex - 1]) =
            (ws.Windows[ws.FocusIndex - 1], ws.Windows[ws.FocusIndex]);
        ws.FocusIndex--;
        WorkspaceContentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void ReorderRight()
    {
        var ws = ActiveWorkspace;
        if (ws.FocusIndex >= ws.Windows.Count - 1) return;
        (ws.Windows[ws.FocusIndex], ws.Windows[ws.FocusIndex + 1]) =
            (ws.Windows[ws.FocusIndex + 1], ws.Windows[ws.FocusIndex]);
        ws.FocusIndex++;
        WorkspaceContentChanged?.Invoke();
        StateChanged?.Invoke();
    }

    public void NextWorkspace()
    {
        int next = _activeWorkspaceId < 9 ? _activeWorkspaceId + 1 : 1;
        SwitchToWorkspace(next);
    }

    public void PreviousWorkspace()
    {
        int prev = _activeWorkspaceId > 1 ? _activeWorkspaceId - 1 : 9;
        SwitchToWorkspace(prev);
    }

    // ---- Off-screen management ----

    private static void ParkWorkspaceOffScreen(Workspace ws)
    {
        foreach (var window in ws.Windows)
        {
            // Skip already-parked windows
            if (window.Frame.Left == -32000) continue;

            NativeMethods.SetWindowPos(window.Hwnd, IntPtr.Zero, -32000, -32000, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Restore ALL windows from ALL workspaces back on-screen.
    /// Crash safety.
    /// </summary>
    public void RestoreAllWindowsOnScreen()
    {
        Log("Restoring all windows on-screen");
        int offset = 0;
        foreach (var ws in _workspaces)
        {
            foreach (var window in ws.Windows)
            {
                // Floating apps — skip
                if (_config.FloatingApps.Contains(window.ProcessName)) continue;

                if (NativeMethods.IsWindow(window.Hwnd))
                {
                    NativeMethods.SetWindowPos(window.Hwnd, IntPtr.Zero,
                        50 + offset * 30, 50 + offset * 30, 0, 0,
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    offset++;
                }
            }
        }
    }

    public (Workspace? Workspace, int Index) FindWindow(IntPtr hwnd)
    {
        foreach (var ws in _workspaces)
        {
            int idx = ws.IndexOf(hwnd);
            if (idx >= 0) return (ws, idx);
        }
        return (null, -1);
    }

    /// <summary>
    /// Force-park all windows on inactive workspaces. Retries multiple times
    /// because some apps (Ghostty, Zen, Teams, Edge) silently ignore
    /// SetWindowPos when they are not the frontmost app.
    /// </summary>
    private void ScheduleOffscreenSweep()
    {
        int[] delays = [100, 300, 800];
        foreach (var delay in delays)
        {
            var captured = delay;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                async () =>
                {
                    await System.Threading.Tasks.Task.Delay(captured);
                    _windowTracker?.BeginProgrammaticUpdate();
                    foreach (var ws in _workspaces)
                    {
                        if (ws.IsVisible) continue;
                        foreach (var window in ws.Windows)
                        {
                            // Check actual position — re-park if still on-screen
                            NativeMethods.GetWindowRect(window.Hwnd, out var rect);
                            if (rect.Left > -30000)
                            {
                                NativeMethods.SetWindowPos(window.Hwnd, IntPtr.Zero,
                                    -32000, -32000, 0, 0,
                                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                            }
                        }
                    }
                    _windowTracker?.EndProgrammaticUpdate();
                });
        }
    }

    /// <summary>
    /// Raise all floating app windows so they stay on top after workspace switches.
    /// </summary>
    private void RaiseFloatingWindows()
    {
        if (_windowTracker == null) return;
        foreach (var window in _windowTracker.AllWindows)
        {
            if (_config.FloatingApps.Contains(window.ProcessName) &&
                NativeMethods.IsWindow(window.Hwnd))
            {
                NativeMethods.BringWindowToTop(window.Hwnd);
            }
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[WorkspaceManager] {message}");
        StreifenLog.Write($"[WorkspaceManager] {message}");
    }
}
