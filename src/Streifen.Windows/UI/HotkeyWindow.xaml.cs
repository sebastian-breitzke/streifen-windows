using System.Windows;

namespace Streifen.Windows.UI;

/// <summary>
/// Hidden window that receives WM_HOTKEY messages.
/// Must exist for RegisterHotKey to have a target HWND.
/// </summary>
public partial class HotkeyWindow : Window
{
    public HotkeyWindow()
    {
        InitializeComponent();
    }
}
