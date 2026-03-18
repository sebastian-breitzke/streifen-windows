namespace Streifen.Windows.Core;

public class Workspace
{
    public int Id { get; }
    public List<TrackedWindow> Windows { get; } = new();
    public double ScrollOffset { get; set; }
    public int FocusIndex { get; set; }
    public bool IsVisible { get; set; }

    public Workspace(int id)
    {
        Id = id;
    }

    public TrackedWindow? FocusedWindow =>
        FocusIndex >= 0 && FocusIndex < Windows.Count ? Windows[FocusIndex] : null;

    public int IndexOf(IntPtr hwnd) =>
        Windows.FindIndex(w => w.Hwnd == hwnd);

    public void InsertAfterFocus(TrackedWindow window)
    {
        // New windows insert at focusIndex + 1, matching macOS behavior
        var insertAt = Math.Min(FocusIndex + 1, Windows.Count);
        Windows.Insert(insertAt, window);
        FocusIndex = insertAt;
    }

    public bool Remove(IntPtr hwnd)
    {
        var idx = IndexOf(hwnd);
        if (idx < 0) return false;

        Windows.RemoveAt(idx);

        // Adjust focus index
        if (Windows.Count == 0)
            FocusIndex = 0;
        else if (FocusIndex >= Windows.Count)
            FocusIndex = Windows.Count - 1;
        else if (idx < FocusIndex)
            FocusIndex--;

        return true;
    }
}
