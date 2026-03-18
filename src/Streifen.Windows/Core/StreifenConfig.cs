namespace Streifen.Windows.Core;

public class StreifenConfig
{
    public double Gap { get; set; } = 10;
    public double PeekWidth { get; set; } = 60;
    public AppSize DefaultSize { get; set; } = AppSize.L;

    /// <summary>Process name → workspace ID (1-9). Only first window of each app goes there.</summary>
    public Dictionary<string, int> PinnedApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process names that follow focus (move to current workspace instead of switching).</summary>
    public HashSet<string> FollowApps { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "calc"
    };

    /// <summary>Process name → default T-shirt size.</summary>
    public Dictionary<string, AppSize> AppSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // Terminals
        ["WindowsTerminal"] = AppSize.S,
        ["powershell"] = AppSize.S,
        ["cmd"] = AppSize.S,
        ["wezterm-gui"] = AppSize.S,

        // Browsers
        ["chrome"] = AppSize.L,
        ["msedge"] = AppSize.L,
        ["firefox"] = AppSize.L,

        // IDEs
        ["devenv"] = AppSize.L,
        ["Code"] = AppSize.L,
        ["rider64"] = AppSize.L,
        ["idea64"] = AppSize.L,

        // Communication
        ["Teams"] = AppSize.M,
        ["OUTLOOK"] = AppSize.M,
        ["slack"] = AppSize.M,

        // Small tools
        ["calc"] = AppSize.XS,

        // File manager
        ["explorer"] = AppSize.S,
    };

    /// <summary>Process names to never track.</summary>
    public HashSet<string> IgnoredProcesses { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "Streifen.Windows",  // ourselves
        "SearchHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "LockApp",
        "TextInputHost",
    };

    public AppSize GetDefaultSize(string processName) =>
        AppSizes.TryGetValue(processName, out var size) ? size : DefaultSize;
}
