using System.Text.Json.Serialization;

namespace ProjectFileHub.Core.Models;

public sealed record AppSettingsState
{
    public bool SpacePreviewEnabled { get; init; } = true;

    public bool InspectorVisible { get; init; } = true;

    public bool FilterRailVisible { get; init; } = true;

    public bool RestoreWorkspace { get; init; } = true;

    public bool StartWithWindows { get; init; }

    public bool CheckForUpdatesOnStartup { get; init; }

    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    public bool CloseToTrayEnabled { get; init; } = true;

    public bool CloseToTrayConfigured { get; init; }

    [JsonIgnore]
    public bool EffectiveCloseToTrayEnabled => !CloseToTrayConfigured || CloseToTrayEnabled;

    public string Theme { get; init; } = AppThemeNames.Midnight;

    public string Density { get; init; } = AppDensityNames.Comfortable;

    public double? TreePaneWidth { get; init; }

    public double? InspectorPaneWidth { get; init; }

    public Dictionary<Guid, ProjectWorkspaceState> ProjectWorkspaces { get; init; } = [];

    public ProjectWorkspaceState? GetWorkspace(Guid projectId) =>
        ProjectWorkspaces.TryGetValue(projectId, out var workspace) ? workspace : null;
}

public sealed record ProjectWorkspaceState
{
    public string? RelativeFolder { get; init; }

    public FileItemCategory? CategoryFilter { get; init; }

    public FileSortField SortField { get; init; } = FileSortField.Name;

    public SortDirection SortDirection { get; init; } = SortDirection.Ascending;

    public bool GridView { get; init; } = true;

    public bool IncludeSubfolders { get; init; }
}

public static class AppThemeNames
{
    public const string Midnight = "Midnight";
    public const string Graphite = "Graphite";
    public const string Light = "Light";

    public static bool IsValid(string? value) =>
        value is Midnight or Graphite or Light;
}

public static class AppDensityNames
{
    public const string Compact = "Compact";
    public const string Comfortable = "Comfortable";
    public const string Spacious = "Spacious";

    public static bool IsValid(string? value) =>
        value is Compact or Comfortable or Spacious;
}
