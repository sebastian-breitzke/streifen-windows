using System.Drawing;
using System.Windows.Forms;
using Streifen.Windows.Services;

namespace Streifen.Windows.UI;

/// <summary>
/// System tray icon showing the active workspace number.
/// Uses Windows Forms NotifyIcon (standard approach for WPF tray apps).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly WorkspaceManager _workspaceManager;
    private readonly App _app;
    private bool _disposed;

    public TrayIcon(WorkspaceManager workspaceManager, App app)
    {
        _workspaceManager = workspaceManager;
        _app = app;

        _notifyIcon = new NotifyIcon
        {
            Text = "Streifen",
            Visible = false,
            ContextMenuStrip = BuildMenu()
        };

        UpdateWorkspaceIndicator(workspaceManager.ActiveWorkspaceId);
    }

    public void Show() => _notifyIcon.Visible = true;

    public void UpdateWorkspaceIndicator(int workspaceId)
    {
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Icon = RenderWorkspaceIcon(workspaceId);
        _notifyIcon.Text = $"Streifen — Workspace {workspaceId}";
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        // Workspace list
        for (int i = 1; i <= 9; i++)
        {
            int wsId = i;
            var item = menu.Items.Add($"Workspace {i}");
            item.Click += (_, _) => _workspaceManager.SwitchToWorkspace(wsId);
        }

        menu.Items.Add(new ToolStripSeparator());

        var resetItem = menu.Items.Add("Reset All Sizes");
        resetItem.Click += (_, _) =>
        {
            var (screenClass, _) = Core.ScreenClassDetector.Detect();
            foreach (var ws in _workspaceManager.AllWorkspaces)
            {
                foreach (var w in ws.Windows)
                {
                    var config = new Core.StreifenConfig();
                    w.ApplySize(config.GetDefaultSize(w.ProcessName), screenClass);
                }
            }
        };

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = menu.Items.Add("Quit Streifen");
        quitItem.Click += (_, _) => _app.RequestShutdown();

        return menu;
    }

    /// <summary>
    /// Render workspace number as a simple icon bitmap.
    /// White text on dark background, 16x16.
    /// </summary>
    private static Icon RenderWorkspaceIcon(int number)
    {
        using var bitmap = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bitmap);

        g.Clear(Color.FromArgb(40, 40, 40));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var font = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);

        var text = number.ToString();
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, brush,
            (16 - size.Width) / 2,
            (16 - size.Height) / 2);

        return Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Icon?.Dispose();
        _notifyIcon.Dispose();
    }
}
