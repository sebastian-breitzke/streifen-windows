namespace Streifen.Windows.Win32;

/// <summary>
/// Manages a SetWinEventHook subscription for window lifecycle events.
/// Uses WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS for safe cross-process monitoring.
/// </summary>
public sealed class WinEventHook : IDisposable
{
    private readonly List<IntPtr> _hooks = new();
    private readonly WinEventDelegate _callback;
    private bool _disposed;

    public event Action<uint, IntPtr>? EventReceived;

    public WinEventHook()
    {
        // Must hold a reference to prevent GC of the delegate
        _callback = OnWinEvent;
    }

    /// <summary>
    /// Install hooks for the event ranges we care about.
    /// Must be called from the UI thread (needs a message loop).
    /// </summary>
    public void Install()
    {
        // Hook range 1: system events (FOREGROUND, MOVESIZEEND)
        var hook1 = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero,
            _callback,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (hook1 != IntPtr.Zero) _hooks.Add(hook1);

        // Hook range 2: object events (CREATE, DESTROY, FOCUS, LOCATIONCHANGE, NAMECHANGE)
        var hook2 = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_CREATE,
            NativeMethods.EVENT_OBJECT_NAMECHANGE,
            IntPtr.Zero,
            _callback,
            0, 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (hook2 != IntPtr.Zero) _hooks.Add(hook2);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only care about top-level window events
        if (idObject != NativeMethods.OBJID_WINDOW || idChild != NativeMethods.CHILDID_SELF)
            return;

        if (hwnd == IntPtr.Zero)
            return;

        EventReceived?.Invoke(eventType, hwnd);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var hook in _hooks)
            NativeMethods.UnhookWinEvent(hook);

        _hooks.Clear();
    }
}
