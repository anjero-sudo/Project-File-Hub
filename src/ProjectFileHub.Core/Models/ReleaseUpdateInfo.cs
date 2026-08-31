namespace ProjectFileHub.Core.Models;

public enum ReleaseUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NoPublishedRelease,
    Failed
}

public sealed record ReleaseUpdateInfo(
    ReleaseUpdateStatus Status,
    Version CurrentVersion,
    Version? LatestVersion = null,
    string? ReleaseName = null,
    string? ReleaseNotes = null,
    DateTimeOffset? PublishedAt = null,
    Uri? ReleasePageUri = null,
    string? ErrorMessage = null);
