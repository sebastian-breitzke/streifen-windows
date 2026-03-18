using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Streifen.Windows.Core;

namespace Streifen.Windows.Services;

/// <summary>
/// Persists and restores workspace state to %APPDATA%\Streifen\state.json.
/// Dual lookup: processName|title → (workspace, sliceCount, appSize).
/// Only restores if state file is less than 15 minutes old.
/// </summary>
public sealed class StateManager
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Streifen");
    private static readonly string StateFile = Path.Combine(StateDir, "state.json");
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Save(WorkspaceManager workspaceManager)
    {
        try
        {
            Directory.CreateDirectory(StateDir);

            var state = new SavedState
            {
                ActiveWorkspace = workspaceManager.ActiveWorkspaceId,
                Timestamp = DateTime.UtcNow,
                ScrollOffsets = new Dictionary<string, double>(),
                Windows = new List<SavedWindow>()
            };

            foreach (var ws in workspaceManager.AllWorkspaces)
            {
                if (ws.ScrollOffset != 0)
                    state.ScrollOffsets[ws.Id.ToString()] = ws.ScrollOffset;

                foreach (var window in ws.Windows)
                {
                    state.Windows.Add(new SavedWindow
                    {
                        Hwnd = $"0x{window.Hwnd.ToInt64():X8}",
                        Workspace = ws.Id,
                        SliceCount = window.SliceCount,
                        AppSize = window.Size.ToString().ToLower(),
                        ProcessName = window.ProcessName,
                        Title = window.Title
                    });
                }
            }

            var json = JsonSerializer.Serialize(state, JsonOptions);
            File.WriteAllText(StateFile, json);
            Log($"Saved state: {state.Windows.Count} windows");
        }
        catch (Exception ex)
        {
            Log($"Save failed: {ex.Message}");
        }
    }

    public bool TryRestore(WorkspaceManager workspaceManager, IReadOnlyList<TrackedWindow> discovered)
    {
        try
        {
            if (!File.Exists(StateFile))
                return false;

            var json = File.ReadAllText(StateFile);
            var state = JsonSerializer.Deserialize<SavedState>(json, JsonOptions);

            if (state == null)
                return false;

            if (DateTime.UtcNow - state.Timestamp > MaxAge)
            {
                Log("State too old, skipping restore");
                return false;
            }

            var (screenClass, _) = ScreenClassDetector.Detect();

            var keyLookup = new Dictionary<string, (int Workspace, int SliceCount, string AppSize)>(StringComparer.OrdinalIgnoreCase);

            foreach (var saved in state.Windows!)
            {
                var key = $"{saved.ProcessName}|{saved.Title}";
                keyLookup[key] = (saved.Workspace, saved.SliceCount, saved.AppSize);
            }

            int matched = 0;
            foreach (var window in discovered)
            {
                var key = $"{window.ProcessName}|{window.Title}";
                if (keyLookup.TryGetValue(key, out var info))
                {
                    if (info.SliceCount > 0)
                    {
                        window.SliceCount = Math.Clamp(info.SliceCount, 1, screenClass.TotalSlices());
                    }
                    else if (Enum.TryParse<AppSize>(info.AppSize, true, out var size))
                    {
                        // Old format fallback: no sliceCount, recalculate from appSize
                        window.ApplySize(size, screenClass);
                    }

                    if (Enum.TryParse<AppSize>(info.AppSize, true, out var parsedSize))
                        window.Size = parsedSize;

                    var ws = workspaceManager.GetWorkspace(info.Workspace);
                    ws.Windows.Add(window);
                    matched++;
                }
                else
                {
                    workspaceManager.ActiveWorkspace.Windows.Add(window);
                }
            }

            if (state.ScrollOffsets != null)
            {
                foreach (var (wsId, offset) in state.ScrollOffsets)
                {
                    if (int.TryParse(wsId, out int id) && id >= 1 && id <= 9)
                        workspaceManager.GetWorkspace(id).ScrollOffset = offset;
                }
            }

            foreach (var ws in workspaceManager.AllWorkspaces)
            {
                if (ws.Windows.Count > 0)
                    ws.FocusIndex = 0;
            }

            Log($"Restored state: {matched}/{discovered.Count} matched");
            return matched > 0;
        }
        catch (Exception ex)
        {
            Log($"Restore failed: {ex.Message}");
            return false;
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[StateManager] {message}");
        StreifenLog.Write($"[StateManager] {message}");
    }

    private class SavedState
    {
        public int ActiveWorkspace { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, double>? ScrollOffsets { get; set; }
        public List<SavedWindow>? Windows { get; set; }
    }

    private class SavedWindow
    {
        public string Hwnd { get; set; } = "";
        public int Workspace { get; set; }
        public int SliceCount { get; set; }
        public string AppSize { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string Title { get; set; } = "";
    }
}
