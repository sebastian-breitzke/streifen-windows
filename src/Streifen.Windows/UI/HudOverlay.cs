using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Streifen.Windows.Core;

namespace Streifen.Windows.UI;

/// <summary>
/// Transient HUD overlay for action feedback.
/// Shows workspace number, slice bar, app defaults, move/reorder feedback.
/// Fixed 280x120 panel. Appears for 0.8s then fades over 0.3s.
/// Adaptive light/dark mode. Brand colors from macOS version.
/// </summary>
public sealed class HudOverlay
{
    private Window? _window;
    private TextBlock? _primaryLabel;
    private TextBlock? _secondaryLabel;
    private Border? _panel;
    private DispatcherTimer? _hideTimer;

    // Brand colors (matching macOS)
    private static readonly Color Blue = (Color)ColorConverter.ConvertFromString("#7BA3C9")!;
    private static readonly Color Gold = (Color)ColorConverter.ConvertFromString("#E8C85A")!;
    private static readonly Color Orange = (Color)ColorConverter.ConvertFromString("#E09560")!;
    private static readonly Color Purple = (Color)ColorConverter.ConvertFromString("#9B82B5")!;
    private static readonly Color Mint = (Color)ColorConverter.ConvertFromString("#7DC9A7")!;

    private const double PanelWidth = 280;
    private const double PanelHeight = 120;
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);

    public HudOverlay()
    {
        _hideTimer = new DispatcherTimer { Interval = ShowDuration };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOut();
        };
    }

    public void ShowWorkspaceSwitch(int workspaceId)
    {
        Show(workspaceId.ToString(), "WORKSPACE", Blue, 56);
    }

    public void ShowSliceResize(int sliceCount, int totalSlices)
    {
        var filled = new string('\u25AE', sliceCount);   // ▮
        var empty = new string('\u25AF', totalSlices - sliceCount); // ▯
        Show($"{filled}{empty}", $"{sliceCount} / {totalSlices} slices", Gold, 18);
    }

    public void ShowAppDefault(AppSize size, string appName)
    {
        Show(size.ToString().ToUpper(), appName, Orange, 36);
    }

    public void ShowMoveToWorkspace(int targetWorkspace)
    {
        Show($"\u2192 {targetWorkspace}", "MOVED", Purple, 36);
    }

    public void ShowReorder(string direction, int position, int total)
    {
        var arrow = direction == "left" ? "\u25C0" : "\u25B6";
        Show($"{arrow} {position}/{total}", "POSITION", Mint, 28);
    }

    public void ShowReset()
    {
        Show("Reset", "", Mint, 28);
    }

    private void Show(string primary, string secondary, Color accent, double fontSize)
    {
        // Cancel any pending hide/fade
        _hideTimer?.Stop();

        if (_window == null)
            CreateWindow();

        bool isDark = IsDarkMode();

        var bgColor = isDark
            ? Color.FromRgb(0x1A, 0x1A, 0x1A)
            : Color.FromRgb(0xFA, 0xF8, 0xF2);

        var textColor = isDark
            ? Colors.White
            : accent;

        var secondaryColor = isDark
            ? Color.FromArgb(180, 255, 255, 255)
            : Color.FromArgb(180, accent.R, accent.G, accent.B);

        var borderColor = isDark
            ? Color.FromArgb(90, accent.R, accent.G, accent.B)
            : Color.FromArgb(64, accent.R, accent.G, accent.B);

        _panel!.Background = new SolidColorBrush(bgColor);
        _panel.BorderBrush = new SolidColorBrush(borderColor);

        _primaryLabel!.Text = primary;
        _primaryLabel.FontSize = fontSize;
        _primaryLabel.Foreground = new SolidColorBrush(textColor);

        _secondaryLabel!.Text = secondary;
        _secondaryLabel.Foreground = new SolidColorBrush(secondaryColor);
        _secondaryLabel.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;

        _window!.Opacity = 1;
        _window.Width = PanelWidth;
        _window.Height = PanelHeight;
        _window.Show();

        // Fixed position: center-top area of managed screen
        var (_, screen) = ScreenClassDetector.Detect();
        var workArea = screen.WorkingArea;

        _window.Left = workArea.Left + (workArea.Width - PanelWidth) / 2;
        _window.Top = workArea.Top + (workArea.Height - PanelHeight) / 2 - workArea.Height * 0.15;

        _hideTimer!.Start();
    }

    private void CreateWindow()
    {
        _primaryLabel = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };

        _secondaryLabel = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(_primaryLabel);
        stack.Children.Add(_secondaryLabel);

        _panel = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(4.5),
            Padding = new Thickness(16, 0, 16, 0),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 4,
                Opacity = 0.3,
                Color = Colors.Black
            }
        };

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.Manual,
            Width = PanelWidth,
            Height = PanelHeight,
            Content = _panel
        };
    }

    private void FadeOut()
    {
        if (_window == null) return;

        var fade = new DoubleAnimation(1, 0, FadeDuration);
        fade.Completed += (_, _) => _window?.Hide();
        _window.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static bool IsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }
}
