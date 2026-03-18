using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Streifen.Windows.Core;
using Streifen.Windows.Services;
using Streifen.Windows.UI;

namespace Streifen.Windows;

public partial class App : Application
{
    private StreifenConfig _config = null!;
    private WindowTracker _windowTracker = null!;
    private WorkspaceManager _workspaceManager = null!;
    private StripLayout _stripLayout = null!;
    private HotkeyManager _hotkeyManager = null!;
    private DebugServer _debugServer = null!;
    private StateManager _stateManager = null!;
    private TrayIcon _trayIcon = null!;
    private HotkeyWindow _hotkeyWindow = null!;
    private HudOverlay _hud = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StreifenLog.Write("App.OnStartup");

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            StreifenLog.Write($"CRASH: {args.ExceptionObject}");
            _workspaceManager?.RestoreAllWindowsOnScreen();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            StreifenLog.Write($"UI CRASH: {args.Exception}");
            _workspaceManager?.RestoreAllWindowsOnScreen();
            args.Handled = true;
        };

        SystemEvents.SessionEnding += (_, _) =>
        {
            StreifenLog.Write("Session ending — restoring windows");
            Shutdown();
        };

        SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            StreifenLog.Write("Display settings changed");
            HandleDisplayChange();
        };

        Initialize();
    }

    private void Initialize()
    {
        // 1. Create components
        _config = new StreifenConfig();
        _windowTracker = new WindowTracker(_config);
        _workspaceManager = new WorkspaceManager(_config);
        _stripLayout = new StripLayout(_config);
        _stateManager = new StateManager();
        _hotkeyManager = new HotkeyManager();
        _hud = new HudOverlay();

        // 2. Wire workspace manager
        _workspaceManager.SetWindowTracker(_windowTracker);

        // 3. Discover windows (no events wired yet)
        var discovered = _windowTracker.DiscoverAll();

        // 4. Try restore state, fall back to initial sort
        if (!_stateManager.TryRestore(_workspaceManager, discovered))
        {
            _workspaceManager.InitialSort(discovered);
        }

        // 5. Layout active workspace
        LayoutAndActivate();

        // 6. Wire window tracker events (AFTER sort)
        _windowTracker.WindowAdded += _workspaceManager.HandleWindowAdded;
        _windowTracker.WindowRemoved += _workspaceManager.HandleWindowRemoved;
        _windowTracker.WindowActivated += _workspaceManager.HandleWindowActivated;
        _windowTracker.WindowFocused += _workspaceManager.HandleWindowFocusedInternal;
        _windowTracker.WindowResized += _workspaceManager.HandleManualResize;

        // 7. Wire workspace manager events → layout + state save
        _workspaceManager.ActiveWorkspaceChanged += () => LayoutAndActivate();
        _workspaceManager.WorkspaceContentChanged += () => LayoutAndActivate();
        _workspaceManager.StateChanged += () => _stateManager.Save(_workspaceManager);

        // 8. Start monitoring window events
        _windowTracker.StartMonitoring();

        // 9. Hidden window for hotkeys
        _hotkeyWindow = new HotkeyWindow();
        _hotkeyWindow.Show();
        _hotkeyManager.RegisterAll(new System.Windows.Interop.WindowInteropHelper(_hotkeyWindow).Handle);
        WireHotkeys();

        // 10. System tray
        _trayIcon = new TrayIcon(_workspaceManager, this);
        _trayIcon.Show();

        // 11. Debug server
        _debugServer = new DebugServer(_workspaceManager, _config);
        _debugServer.Start();

        StreifenLog.Write("Initialization complete");
    }

    private void WireHotkeys()
    {
        _hotkeyManager.SwitchWorkspace += id =>
        {
            _workspaceManager.SwitchToWorkspace(id);
            _hud.ShowWorkspaceSwitch(id);
        };

        _hotkeyManager.MoveToWorkspace += id =>
        {
            _workspaceManager.MoveWindowToWorkspace(id);
            _hud.ShowMoveToWorkspace(id);
        };

        _hotkeyManager.FocusLeft += () =>
        {
            _workspaceManager.FocusLeft();
            ActivateFocusedWindow();
        };
        _hotkeyManager.FocusRight += () =>
        {
            _workspaceManager.FocusRight();
            ActivateFocusedWindow();
        };

        _hotkeyManager.ReorderLeft += () =>
        {
            _workspaceManager.ReorderLeft();
            var ws = _workspaceManager.ActiveWorkspace;
            _hud.ShowReorder("left", ws.FocusIndex + 1, ws.Windows.Count);
        };
        _hotkeyManager.ReorderRight += () =>
        {
            _workspaceManager.ReorderRight();
            var ws = _workspaceManager.ActiveWorkspace;
            _hud.ShowReorder("right", ws.FocusIndex + 1, ws.Windows.Count);
        };

        _hotkeyManager.NextWorkspace += () => _workspaceManager.NextWorkspace();
        _hotkeyManager.PreviousWorkspace += () => _workspaceManager.PreviousWorkspace();
        _hotkeyManager.MoveToNextWorkspace += () =>
        {
            int next = _workspaceManager.ActiveWorkspaceId < 9 ? _workspaceManager.ActiveWorkspaceId + 1 : 1;
            _workspaceManager.MoveWindowToWorkspace(next);
            _hud.ShowMoveToWorkspace(next);
        };
        _hotkeyManager.MoveToPreviousWorkspace += () =>
        {
            int prev = _workspaceManager.ActiveWorkspaceId > 1 ? _workspaceManager.ActiveWorkspaceId - 1 : 9;
            _workspaceManager.MoveWindowToWorkspace(prev);
            _hud.ShowMoveToWorkspace(prev);
        };

        _hotkeyManager.SetSliceCount += slices =>
        {
            var (screenClass, _) = ScreenClassDetector.Detect();
            var window = _workspaceManager.ActiveWorkspace.FocusedWindow;
            if (window != null)
            {
                window.SetSliceCount(slices, screenClass);
                LayoutAndActivate();
                _stateManager.Save(_workspaceManager);
                _hud.ShowSliceResize(window.SliceCount, screenClass.TotalSlices());
            }
        };

        _hotkeyManager.StepSliceCount += delta =>
        {
            var (screenClass, _) = ScreenClassDetector.Detect();
            var window = _workspaceManager.ActiveWorkspace.FocusedWindow;
            if (window != null)
            {
                window.SetSliceCount(window.SliceCount + delta, screenClass);
                LayoutAndActivate();
                _stateManager.Save(_workspaceManager);
                _hud.ShowSliceResize(window.SliceCount, screenClass.TotalSlices());
            }
        };

        _hotkeyManager.SetAppDefaultSize += size =>
        {
            var (screenClass, _) = ScreenClassDetector.Detect();
            var window = _workspaceManager.ActiveWorkspace.FocusedWindow;
            if (window != null)
            {
                _config.AppSizes[window.ProcessName] = size;
                window.ApplySize(size, screenClass);
                LayoutAndActivate();
                _stateManager.Save(_workspaceManager);
                _hud.ShowAppDefault(size, window.ProcessName);
            }
        };

        _hotkeyManager.ResetSizes += () =>
        {
            var (screenClass, _) = ScreenClassDetector.Detect();
            foreach (var ws in _workspaceManager.AllWorkspaces)
            {
                foreach (var w in ws.Windows)
                {
                    var defaultSize = _config.GetDefaultSize(w.ProcessName);
                    w.ApplySize(defaultSize, screenClass);
                }
            }
            LayoutAndActivate();
            _stateManager.Save(_workspaceManager);
            _hud.ShowReset();
        };

        _hotkeyManager.DebugDump += () =>
        {
            var focused = Win32.NativeMethods.GetForegroundWindow();
            var style = Win32.NativeMethods.GetWindowLong(focused, Win32.NativeMethods.GWL_STYLE);
            var exStyle = Win32.NativeMethods.GetWindowLong(focused, Win32.NativeMethods.GWL_EXSTYLE);
            var title = Win32.NativeMethods.GetWindowTitle(focused);
            var className = Win32.NativeMethods.GetWindowClassName(focused);
            var processName = Win32.NativeMethods.GetProcessName(focused);

            StreifenLog.Write($"DEBUG DUMP: hwnd=0x{focused.ToInt64():X8} process={processName} " +
                $"class={className} title={title} style=0x{style:X8} exStyle=0x{exStyle:X8}");
        };
    }

    private void LayoutAndActivate()
    {
        _windowTracker.BeginProgrammaticUpdate();

        var ws = _workspaceManager.ActiveWorkspace;
        _stripLayout.EnsureWindowVisible(ws);
        _stripLayout.Layout(ws);
        ActivateFocusedWindow();
        _trayIcon?.UpdateWorkspaceIndicator(_workspaceManager.ActiveWorkspaceId);

        _windowTracker.EndProgrammaticUpdate();
    }

    private void ActivateFocusedWindow()
    {
        var ws = _workspaceManager.ActiveWorkspace;
        if (ws.Windows.Count == 0) return;

        // Raise ALL workspace windows first (Electron apps need this)
        foreach (var window in ws.Windows)
        {
            if (Win32.NativeMethods.IsWindow(window.Hwnd))
                Win32.NativeMethods.BringWindowToTop(window.Hwnd);
        }

        var focused = ws.FocusedWindow;
        if (focused != null && Win32.NativeMethods.IsWindow(focused.Hwnd))
        {
            Win32.NativeMethods.SetForegroundWindow(focused.Hwnd);
        }
    }

    private void HandleDisplayChange()
    {
        var (screenClass, _) = ScreenClassDetector.Detect();

        // Recalculate all slice counts for new screen class
        foreach (var ws in _workspaceManager.AllWorkspaces)
        {
            foreach (var w in ws.Windows)
                w.ApplySize(w.Size, screenClass);
        }

        _stripLayout.ClampScrollOffset(_workspaceManager.ActiveWorkspace);
        LayoutAndActivate();
    }

    public void RequestShutdown()
    {
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StreifenLog.Write("Shutting down");

        _stateManager?.Save(_workspaceManager);
        _workspaceManager?.RestoreAllWindowsOnScreen();

        _hotkeyManager?.Dispose();
        _debugServer?.Dispose();
        _windowTracker?.Dispose();
        _trayIcon?.Dispose();

        StreifenLog.Write("=== Streifen Windows stopped ===");
        base.OnExit(e);
    }
}
