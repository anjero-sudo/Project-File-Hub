namespace ProjectFileHub.Core.Services;

public static class MarkdownProjectLinkResolver
{
    public static string ResolveLocalPath(
        string projectRoot,
        string sourceFilePath,
        string href)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);

        var separatorIndex = href.IndexOfAny(['#', '?']);
        var pathPart = separatorIndex >= 0 ? href[..separatorIndex] : href;
        pathPart = Uri.UnescapeDataString(pathPart.Trim())
            .Replace('/', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(pathPart) || Path.IsPathRooted(pathPart))
        {
            throw new UnauthorizedAccessException("绝对路径或空路径不能作为项目内链接。");
        }

        var boundary = new PathBoundary(projectRoot);
        var safeSource = boundary.EnsureSafe(sourceFilePath);
        var sourceFolder = Path.GetDirectoryName(safeSource) ?? boundary.RootPath;
        var candidate = PathBoundary.NormalizeRoot(Path.Combine(sourceFolder, pathPart));
        return boundary.EnsureSafe(candidate);
    }
}
