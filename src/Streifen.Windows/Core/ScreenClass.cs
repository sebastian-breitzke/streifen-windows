using System.Windows.Forms;

namespace Streifen.Windows.Core;

public enum ScreenClass
{
    Laptop,
    Desktop,
    Ultrawide
}

public static class ScreenClassExtensions
{
    /// <summary>
    /// Total slice count for this screen class.
    /// Laptop=4, Desktop=6, Ultrawide=8.
    /// </summary>
    public static int TotalSlices(this ScreenClass sc) => sc switch
    {
        ScreenClass.Laptop => 4,
        ScreenClass.Desktop => 6,
        ScreenClass.Ultrawide => 8,
        _ => 6
    };
}

public static class ScreenClassDetector
{
    /// <summary>
    /// Detect screen class from the widest connected screen (not primary).
    /// Matches macOS behavior: always manage the widest screen.
    /// </summary>
    public static (ScreenClass Class, Screen Screen) Detect()
    {
        var screen = Screen.AllScreens
            .OrderByDescending(s => s.WorkingArea.Width)
            .First();

        var aspect = (double)screen.WorkingArea.Width / screen.WorkingArea.Height;

        var cls = aspect switch
        {
            < 1.5 => ScreenClass.Laptop,
            >= 2.3 => ScreenClass.Ultrawide,
            _ => ScreenClass.Desktop
        };

        return (cls, screen);
    }
}
