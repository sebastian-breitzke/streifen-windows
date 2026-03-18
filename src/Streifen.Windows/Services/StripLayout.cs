using Streifen.Windows.Core;
using Streifen.Windows.Win32;

namespace Streifen.Windows.Services;

/// <summary>
/// Horizontal strip layout engine using slice-based grid.
/// Width = screenWidth * (sliceCount / totalSlices) - 2 * gap.
/// </summary>
public sealed class StripLayout
{
    private readonly StreifenConfig _config;

    public StripLayout(StreifenConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Layout all windows in a workspace on the managed screen.
    /// </summary>
    public void Layout(Workspace workspace)
    {
        if (workspace.Windows.Count == 0) return;

        var (screenClass, screen) = ScreenClassDetector.Detect();
        var workArea = screen.WorkingArea;
        int totalSlices = screenClass.TotalSlices();

        double gap = _config.Gap;
        double peek = _config.PeekWidth;
        bool hasNeighbors = workspace.Windows.Count > 1;

        double maxWidth = hasNeighbors
            ? workArea.Width - 2 * gap - 2 * peek
            : workArea.Width - 2 * gap;

        double x = gap;
        for (int i = 0; i < workspace.Windows.Count; i++)
        {
            var window = workspace.Windows[i];
            double targetWidth = workArea.Width * ((double)window.SliceCount / totalSlices) - 2 * gap;

            targetWidth = Math.Min(targetWidth, maxWidth);
            targetWidth = Math.Max(targetWidth, 200);

            window.VirtualX = x;

            double visualX = x + workspace.ScrollOffset;

            int winX = (int)(workArea.Left + visualX);
            int winY = (int)(workArea.Top + gap);
            int winWidth = (int)targetWidth;
            int winHeight = (int)(workArea.Height - 2 * gap);

            if (winX + winWidth < workArea.Left || winX > workArea.Right)
            {
                NativeMethods.SetWindowPos(window.Hwnd, IntPtr.Zero,
                    -32000, -32000, winWidth, winHeight,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            else
            {
                NativeMethods.SetWindowPos(window.Hwnd, IntPtr.Zero,
                    winX, winY, winWidth, winHeight,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }

            window.Frame = new RECT
            {
                Left = winX,
                Top = winY,
                Right = winX + winWidth,
                Bottom = winY + winHeight
            };

            x += targetWidth + gap;
        }
    }

    /// <summary>
    /// Clamp scroll offset to valid range.
    /// </summary>
    public void ClampScrollOffset(Workspace workspace)
    {
        var (screenClass, screen) = ScreenClassDetector.Detect();
        double screenWidth = screen.WorkingArea.Width;

        double totalWidth = CalculateTotalWidth(workspace, screenWidth, screenClass);

        if (totalWidth + workspace.ScrollOffset < screenWidth)
            workspace.ScrollOffset = screenWidth - totalWidth;

        if (workspace.ScrollOffset > 0)
            workspace.ScrollOffset = 0;
    }

    /// <summary>
    /// Ensure the focused window is fully visible, with peek margins for neighbors.
    /// </summary>
    public void EnsureWindowVisible(Workspace workspace)
    {
        if (workspace.Windows.Count == 0 || workspace.FocusIndex < 0)
            return;

        var (screenClass, screen) = ScreenClassDetector.Detect();
        var workArea = screen.WorkingArea;
        int totalSlices = screenClass.TotalSlices();
        double gap = _config.Gap;
        double peek = _config.PeekWidth;
        const double tolerance = 5;

        var window = workspace.Windows[workspace.FocusIndex];

        double visualLeft = window.VirtualX + workspace.ScrollOffset;
        double windowWidth = workArea.Width * ((double)window.SliceCount / totalSlices) - 2 * gap;
        windowWidth = Math.Max(windowWidth, 200);
        double visualRight = visualLeft + windowWidth;

        double leftBound = (workspace.FocusIndex > 0) ? peek + gap : gap;
        double rightBound = workArea.Width - ((workspace.FocusIndex < workspace.Windows.Count - 1) ? peek + gap : gap);

        if (visualLeft < leftBound - tolerance)
        {
            workspace.ScrollOffset += (leftBound - visualLeft);
        }
        else if (visualRight > rightBound + tolerance)
        {
            workspace.ScrollOffset -= (visualRight - rightBound);
        }

        ClampScrollOffset(workspace);
    }

    /// <summary>
    /// Get the width of one slice in pixels for the current screen.
    /// Used by manual resize snap.
    /// </summary>
    public static double GetSliceWidth()
    {
        var (screenClass, screen) = ScreenClassDetector.Detect();
        return (double)screen.WorkingArea.Width / screenClass.TotalSlices();
    }

    private double CalculateTotalWidth(Workspace workspace, double screenWidth, ScreenClass screenClass)
    {
        int totalSlices = screenClass.TotalSlices();
        double gap = _config.Gap;
        double total = gap;

        foreach (var window in workspace.Windows)
        {
            double width = screenWidth * ((double)window.SliceCount / totalSlices) - 2 * gap;
            width = Math.Max(width, 200);
            total += width + gap;
        }

        return total;
    }
}
