using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Streifen.Windows.Core;

namespace Streifen.Windows.Services;

/// <summary>
/// HTTP debug server on localhost:22222.
/// Exposes JSON state for debugging and tooling.
/// </summary>
public sealed class DebugServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly WorkspaceManager _workspaceManager;
    private readonly StreifenConfig _config;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public DebugServer(WorkspaceManager workspaceManager, StreifenConfig config)
    {
        _workspaceManager = workspaceManager;
        _config = config;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://localhost:22222/");
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _cts = new CancellationTokenSource();
            _listenTask = ListenAsync(_cts.Token);
            Log("Started on http://localhost:22222");
        }
        catch (Exception ex)
        {
            Log($"Failed to start: {ex.Message}");
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequest(context);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        context.Response.ContentType = "application/json";

        try
        {
            string json = path switch
            {
                "/state" => SerializeFullState(),
                "/active" => SerializeWorkspace(_workspaceManager.ActiveWorkspace),
                "/windows" => SerializeAllWindows(),
                _ when path.StartsWith("/workspace/") => HandleWorkspaceRequest(path),
                _ => JsonSerializer.Serialize(new { error = "Not found", endpoints = new[] { "/state", "/active", "/windows", "/workspace/{1-9}" } }, JsonOptions)
            };

            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            var errorBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions));
            context.Response.StatusCode = 500;
            await context.Response.OutputStream.WriteAsync(errorBytes);
        }
        finally
        {
            context.Response.Close();
        }
    }

    private string HandleWorkspaceRequest(string path)
    {
        var parts = path.Split('/');
        if (parts.Length >= 3 && int.TryParse(parts[2], out int id) && id >= 1 && id <= 9)
            return SerializeWorkspace(_workspaceManager.GetWorkspace(id));

        return JsonSerializer.Serialize(new { error = "Invalid workspace ID (1-9)" }, JsonOptions);
    }

    private string SerializeFullState()
    {
        var (screenClass, screen) = ScreenClassDetector.Detect();

        var state = new
        {
            activeWorkspace = _workspaceManager.ActiveWorkspaceId,
            screenClass = screenClass.ToString().ToLower(),
            totalSlices = screenClass.TotalSlices(),
            screen = new
            {
                width = screen.WorkingArea.Width,
                height = screen.WorkingArea.Height,
                x = screen.WorkingArea.X,
                y = screen.WorkingArea.Y
            },
            config = new
            {
                gap = _config.Gap,
                peekWidth = _config.PeekWidth,
                defaultSize = _config.DefaultSize.ToString().ToLower()
            },
            workspaces = _workspaceManager.AllWorkspaces.Select(SerializeWorkspaceData).ToArray()
        };

        return JsonSerializer.Serialize(state, JsonOptions);
    }

    private string SerializeWorkspace(Workspace ws)
    {
        return JsonSerializer.Serialize(SerializeWorkspaceData(ws), JsonOptions);
    }

    private object SerializeWorkspaceData(Workspace ws)
    {
        var (screenClass, screen) = ScreenClassDetector.Detect();
        int totalSlices = screenClass.TotalSlices();

        return new
        {
            id = ws.Id,
            isVisible = ws.IsVisible,
            scrollOffset = ws.ScrollOffset,
            focusIndex = ws.FocusIndex,
            windowCount = ws.Windows.Count,
            windows = ws.Windows.Select((w, i) => new
            {
                hwnd = $"0x{w.Hwnd.ToInt64():X8}",
                process = w.ProcessName,
                title = w.Title,
                appSize = w.Size.ToString().ToLower(),
                sliceCount = w.SliceCount,
                totalSlices,
                frame = new { x = w.Frame.Left, y = w.Frame.Top, width = w.Frame.Width, height = w.Frame.Height },
                virtualX = Math.Round(w.VirtualX, 1),
                workspace = ws.Id,
                index = i,
                focused = i == ws.FocusIndex,
                offscreen = w.Frame.Left <= -30000 ||
                            w.Frame.Left + w.Frame.Width < screen.WorkingArea.Left ||
                            w.Frame.Left > screen.WorkingArea.Right
            }).ToArray()
        };
    }

    private string SerializeAllWindows()
    {
        var (screenClass, _) = ScreenClassDetector.Detect();
        int totalSlices = screenClass.TotalSlices();

        var allWindows = _workspaceManager.AllWorkspaces
            .SelectMany(ws => ws.Windows.Select((w, i) => new
            {
                hwnd = $"0x{w.Hwnd.ToInt64():X8}",
                process = w.ProcessName,
                title = w.Title,
                appSize = w.Size.ToString().ToLower(),
                sliceCount = w.SliceCount,
                totalSlices,
                workspace = ws.Id
            }))
            .ToArray();

        return JsonSerializer.Serialize(allWindows, JsonOptions);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _listener.Stop();
        _listener.Close();
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[DebugServer] {message}");
        StreifenLog.Write($"[DebugServer] {message}");
    }
}
