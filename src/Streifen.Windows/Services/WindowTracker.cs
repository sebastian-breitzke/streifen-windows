using System.Diagnostics;
using System.Windows.Threading;
using Streifen.Windows.Core;
using Streifen.Windows.Win32;

namespace Streifen.Windows.Services;

/// <summary>
/// Discovers and monitors Win32 windows.
/// Uses EnumWindows for initial discovery, WinEventHook for lifecycle monitoring.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    private readonly StreifenConfig _config;
    private readonly Dictionary<IntPtr, TrackedWindow> _windows = new();
    private readonly WinEventHook _eventHook;
    private readonly DispatcherTimer _livenessTimer;
    private readonly HashSet<uint> _recentlyLaunchedPids = new();

    public bool IsUpdating { get; set; }

    public event Action<TrackedWindow>? WindowAdded;
    public event Action<IntPtr>? WindowRemoved;
    public event Action<IntPtr>? WindowActivated;
    public event Action<IntPtr>? WindowFocused;

    /// <summary>Fired when user manually resized a window (width delta > 10px).</summary>
    public event Action<IntPtr>? WindowResized;

    public WindowTracker(StreifenConfig config)
    {
        _config = config;
        _eventHook = new WinEventHook();

        _livenessTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _livenessTimer.Tick += OnLivenessCheck;
    }

    public List<TrackedWindow> DiscoverAll()
    {
        var discovered = new List<TrackedWindow>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (ShouldTrack(hwnd))
            {
                var window = CreateTrackedWindow(hwnd);
                if (window != null)
                {
                    _windows[hwnd] = window;
                    discovered.Add(window);
                }
            }
            return true;
        }, IntPtr.Zero);

        Log($"Discovered {discovered.Count} windows");
        return discovered;
    }

    public void StartMonitoring()
    {
        _eventHook.EventReceived += OnWinEvent;
        _eventHook.Install();
        _livenessTimer.Start();
        Log("Monitoring started");
    }

    public IReadOnlyList<TrackedWindow> AllWindows => _windows.Values.ToList();

    public TrackedWindow? GetWindow(IntPtr hwnd) =>
        _windows.TryGetValue(hwnd, out var w) ? w : null;

    public bool IsTracked(IntPtr hwnd) => _windows.ContainsKey(hwnd);

    // ---- Event handling ----

    private void OnWinEvent(uint eventType, IntPtr hwnd)
    {
        switch (eventType)
        {
            case NativeMethods.EVENT_OBJECT_CREATE:
                HandleWindowCreated(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_DESTROY:
                // Allow destroy even during programmatic updates —
                // window closures during layout must not be silently dropped.
                HandleWindowDestroyed(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                if (!IsUpdating && _windows.ContainsKey(hwnd))
                    WindowActivated?.Invoke(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_FOCUS:
                if (!IsUpdating && _windows.ContainsKey(hwnd))
                    WindowFocused?.Invoke(hwnd);
                break;

            case NativeMethods.EVENT_SYSTEM_MOVESIZEEND:
                if (!IsUpdating && _windows.TryGetValue(hwnd, out var movedWindow))
                {
                    var oldFrame = movedWindow.Frame;
                    movedWindow.Frame = NativeMethods.GetWindowFrame(hwnd);

                    // Detect manual resize: width changed by > 10px
                    if (Math.Abs(movedWindow.Frame.Width - oldFrame.Width) > 10)
                    {
                        WindowResized?.Invoke(hwnd);
                    }
                }
                break;

            case NativeMethods.EVENT_OBJECT_LOCATIONCHANGE:
                if (IsUpdating) break;
                if (_windows.ContainsKey(hwnd))
                    _windows[hwnd].Frame = NativeMethods.GetWindowFrame(hwnd);
                break;

            case NativeMethods.EVENT_OBJECT_NAMECHANGE:
                if (_windows.TryGetValue(hwnd, out var namedWindow))
                    namedWindow.ReadTitle();
                break;
        }
    }

    private void HandleWindowCreated(IntPtr hwnd)
    {
        if (_windows.ContainsKey(hwnd))
            return;
        if (IsUpdating) return;

        uint pid = NativeMethods.GetProcessId(hwnd);
        if (!_recentlyLaunchedPids.Contains(pid))
        {
            _recentlyLaunchedPids.Add(pid);
            DelayedDiscovery(hwnd, pid, TimeSpan.FromMilliseconds(500));
        }
        else
        {
            // Tab tear-off / same process: 300ms retry
            DelayedDiscovery(hwnd, pid, TimeSpan.FromMilliseconds(300));
        }
    }

    private async void DelayedDiscovery(IntPtr hwnd, uint pid, TimeSpan delay)
    {
        await Task.Delay(delay);

        if (!NativeMethods.IsWindow(hwnd) || _windows.ContainsKey(hwnd))
            return;

        if (ShouldTrack(hwnd))
        {
            var window = CreateTrackedWindow(hwnd);
            if (window != null)
            {
                _windows[hwnd] = window;
                Log($"New window: {window}");
                WindowAdded?.Invoke(window);
            }
        }
    }

    private void HandleWindowDestroyed(IntPtr hwnd)
    {
        if (!_windows.Remove(hwnd, out var window))
            return;

        Log($"Window destroyed: {window}");
        WindowRemoved?.Invoke(hwnd);

        uint pid = NativeMethods.GetProcessId(hwnd);
        if (pid != 0)
            DeferredCleanup(pid);
    }

    private async void DeferredCleanup(uint pid)
    {
        await Task.Delay(100);

        var stale = _windows.Values
            .Where(w => NativeMethods.GetProcessId(w.Hwnd) == pid && !w.IsAlive())
            .Select(w => w.Hwnd)
            .ToList();

        foreach (var hwnd in stale)
        {
            if (_windows.Remove(hwnd, out var window))
            {
                Log($"Deferred cleanup: {window}");
                WindowRemoved?.Invoke(hwnd);
            }
        }
    }

    // ---- Liveness ----

    private void OnLivenessCheck(object? sender, EventArgs e)
    {
        var dead = _windows.Values
            .Where(w => !w.IsAlive())
            .ToList();

        foreach (var window in dead)
        {
            // Only remove if process is also gone (prevents false removal of off-screen windows)
            bool processGone;
            try
            {
                var proc = Process.GetProcessById((int)NativeMethods.GetProcessId(window.Hwnd));
                processGone = proc.HasExited;
            }
            catch
            {
                processGone = true;
            }

            if (processGone && _windows.Remove(window.Hwnd, out _))
            {
                Log($"Liveness: removed {window}");
                WindowRemoved?.Invoke(window.Hwnd);
            }
        }
    }

    // ---- Filtering ----

    private bool ShouldTrack(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd))
            return false;

        if (NativeMethods.IsCloaked(hwnd))
            return false;

        string title = NativeMethods.GetWindowTitle(hwnd);
        if (string.IsNullOrWhiteSpace(title))
            return false;

        int style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
        int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);

        if ((style & WindowStyles.WS_CHILD) != 0)
            return false;

        if ((exStyle & WindowStyles.WS_EX_TOOLWINDOW) != 0 &&
            (exStyle & WindowStyles.WS_EX_APPWINDOW) == 0)
            return false;

        if ((exStyle & WindowStyles.WS_EX_NOACTIVATE) != 0)
            return false;

        NativeMethods.GetWindowRect(hwnd, out var rect);

        if (rect.Width < 100 || rect.Height < 100)
            return false;

        if (rect.Width < 400 && rect.Height < 400)
            return false;

        string processName = NativeMethods.GetProcessName(hwnd);
        if (string.IsNullOrEmpty(processName))
            return false;

        if (_config.IgnoredProcesses.Contains(processName))
            return false;

        return true;
    }

    private TrackedWindow? CreateTrackedWindow(IntPtr hwnd)
    {
        var window = new TrackedWindow(hwnd);
        window.Discover();

        if (string.IsNullOrEmpty(window.ProcessName))
            return null;

        var (screenClass, _) = ScreenClassDetector.Detect();
        var defaultSize = _config.GetDefaultSize(window.ProcessName);
        window.ApplySize(defaultSize, screenClass);

        return window;
    }

    // ---- Programmatic update control ----

    public void BeginProgrammaticUpdate() => IsUpdating = true;

    public async void EndProgrammaticUpdate()
    {
        await Task.Delay(50);
        IsUpdating = false;
    }

    private static void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        Debug.WriteLine($"[WindowTracker {timestamp}] {message}");
        StreifenLog.Write($"[WindowTracker] {message}");
    }

    public void Dispose()
    {
        _livenessTimer.Stop();
        _eventHook.Dispose();
        _windows.Clear();
    }
}
