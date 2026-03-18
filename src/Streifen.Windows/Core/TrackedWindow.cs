using Streifen.Windows.Win32;

namespace Streifen.Windows.Core;

public class TrackedWindow
{
    public IntPtr Hwnd { get; }
    public string ProcessName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Title { get; set; } = "";
    public RECT Frame { get; set; }
    public double VirtualX { get; set; }
    public int SliceCount { get; set; }
    public AppSize Size { get; set; }

    public TrackedWindow(IntPtr hwnd)
    {
        Hwnd = hwnd;
    }

    /// <summary>
    /// Initialize window properties from Win32 APIs.
    /// No cached resizability — always attempt resize (matches macOS Rider fix).
    /// </summary>
    public void Discover()
    {
        ProcessName = NativeMethods.GetProcessName(Hwnd);
        ClassName = NativeMethods.GetWindowClassName(Hwnd);
        Title = NativeMethods.GetWindowTitle(Hwnd);
        Frame = NativeMethods.GetWindowFrame(Hwnd);
    }

    /// <summary>
    /// Apply a T-shirt size, recalculating slice count for the current screen class.
    /// </summary>
    public void ApplySize(AppSize size, ScreenClass screenClass)
    {
        Size = size;
        SliceCount = size.Slices(screenClass);
    }

    /// <summary>
    /// Set slice count directly, clamped to valid range.
    /// </summary>
    public void SetSliceCount(int count, ScreenClass screenClass)
    {
        int total = screenClass.TotalSlices();
        SliceCount = Math.Clamp(count, 1, total);
    }

    /// <summary>
    /// Read current title from Win32. Cheap enough to call live.
    /// </summary>
    public string ReadTitle()
    {
        Title = NativeMethods.GetWindowTitle(Hwnd);
        return Title;
    }

    /// <summary>
    /// Check if the window handle is still valid.
    /// </summary>
    public bool IsAlive() => NativeMethods.IsWindow(Hwnd);

    public override string ToString() => $"[{Hwnd}] {ProcessName}: {Title} ({Size}, {SliceCount}sl)";
}
