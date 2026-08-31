using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

/// <summary>
/// Checks the latest stable GitHub Release without downloading or executing assets.
/// </summary>
public sealed class GitHubReleaseUpdateService
{
    public const string RepositoryOwner = "anjero-sudo";
    public const string RepositoryName = "Project-File-Hub";
    public static readonly Uri ReleasesPageUri = new(
        $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases");

    private static readonly Uri LatestReleaseApiUri = new(
        $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    public async Task<ReleaseUpdateInfo> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ProjectFileHub", NormalizeVersion(currentVersion).ToString(3)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new ReleaseUpdateInfo(
                    ReleaseUpdateStatus.NoPublishedRelease,
                    NormalizeVersion(currentVersion),
                    ReleasePageUri: ReleasesPageUri);
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            var tagName = GetOptionalString(root, "tag_name");
            if (!TryParseReleaseVersion(tagName, out var latestVersion))
            {
                return Failed(currentVersion, "GitHub Release 的版本标签无法识别。请使用 v0.0.0 形式的标签。");
            }

            var releasePageUri = GetTrustedReleasePageUri(GetOptionalString(root, "html_url"));
            var publishedAt = GetOptionalDateTimeOffset(root, "published_at");
            var normalizedCurrent = NormalizeVersion(currentVersion);
            var normalizedLatest = NormalizeVersion(latestVersion);
            var status = normalizedLatest > normalizedCurrent
                ? ReleaseUpdateStatus.UpdateAvailable
                : ReleaseUpdateStatus.UpToDate;

            return new ReleaseUpdateInfo(
                status,
                normalizedCurrent,
                normalizedLatest,
                GetOptionalString(root, "name") ?? tagName,
                GetOptionalString(root, "body"),
                publishedAt,
                releasePageUri);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(currentVersion, "检查更新超时，请稍后重试。");
        }
        catch (HttpRequestException exception)
        {
            return Failed(currentVersion, $"无法连接 GitHub：{exception.Message}");
        }
        catch (JsonException)
        {
            return Failed(currentVersion, "GitHub 返回了无法读取的发布信息。");
        }
    }

    public static bool TryParseReleaseVersion(string? tagName, out Version version)
    {
        var candidate = tagName?.Trim();
        if (candidate is not null && candidate.StartsWith('v'))
        {
            candidate = candidate[1..];
        }

        if (Version.TryParse(candidate, out var parsed) && parsed.Major >= 0)
        {
            version = NormalizeVersion(parsed);
            return true;
        }

        version = new Version(0, 0, 0);
        return false;
    }

    public static bool IsTrustedReleasePage(Uri? uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith(
            $"/{RepositoryOwner}/{RepositoryName}/releases/",
            StringComparison.OrdinalIgnoreCase);

    private static ReleaseUpdateInfo Failed(Version currentVersion, string message) =>
        new(
            ReleaseUpdateStatus.Failed,
            NormalizeVersion(currentVersion),
            ReleasePageUri: ReleasesPageUri,
            ErrorMessage: message);

    private static Uri GetTrustedReleasePageUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTrustedReleasePage(uri)
            ? uri
            : ReleasesPageUri;

    private static string? GetOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? GetOptionalDateTimeOffset(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
        && property.TryGetDateTimeOffset(out var value)
            ? value
            : null;

    private static Version NormalizeVersion(Version version) =>
        new(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
}
