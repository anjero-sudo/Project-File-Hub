using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed class FileSystemBrowser
{
    public string? GetParentFolder(string projectRoot, string folderPath)
    {
        var boundary = new PathBoundary(projectRoot);
        var safeFolder = boundary.EnsureSafe(folderPath);
        if (string.Equals(PathBoundary.NormalizeRoot(safeFolder), PathBoundary.NormalizeRoot(projectRoot),
                StringComparison.OrdinalIgnoreCase)) return null;
        var parent = Path.GetDirectoryName(PathBoundary.NormalizeRoot(safeFolder));
        return parent is null ? null : boundary.EnsureSafe(parent);
    }

    public IReadOnlyList<FileSystemItem> GetItems(
        string projectRoot,
        string folderPath,
        FileQueryOptions options) =>
        GetItems(projectRoot, folderPath, options, progress: null, CancellationToken.None);

    public IReadOnlyList<FileSystemItem> GetItems(
        string projectRoot,
        string folderPath,
        FileQueryOptions options,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var boundary = new PathBoundary(projectRoot);
        var safeFolder = boundary.EnsureSafe(folderPath);

        if (!Directory.Exists(safeFolder))
        {
            throw new DirectoryNotFoundException(safeFolder);
        }

        var items = new List<FileSystemItem>();
        var scannedCount = 0;

        foreach (var path in Directory.EnumerateFileSystemEntries(safeFolder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scannedCount++;
            if (scannedCount == 1 || scannedCount % 50 == 0)
            {
                progress?.Report(scannedCount);
            }

            if (!boundary.IsSafeExistingPath(path))
            {
                continue;
            }

            try
            {
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var extension = isDirectory ? string.Empty : Path.GetExtension(path);
                var category = FileCategoryClassifier.Classify(extension, isDirectory);

                if (options.Category is FileItemCategory categoryFilter && category != categoryFilter)
                {
                    continue;
                }

                FileSystemInfo info = isDirectory
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);

                if (!options.MatchesName(info.Name))
                {
                    continue;
                }

                items.Add(new FileSystemItem(
                    info.Name,
                    info.FullName,
                    isDirectory,
                    isDirectory ? null : ((FileInfo)info).Length,
                    info.LastWriteTimeUtc,
                    info.CreationTimeUtc,
                    extension,
                    category));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // An entry may disappear or become inaccessible while its parent is being enumerated.
            }
        }

        progress?.Report(scannedCount);
        cancellationToken.ThrowIfCancellationRequested();

        cancellationToken.ThrowIfCancellationRequested();
        return FileItemSort.Apply(items, options, directoriesFirst: true);
    }

    public IReadOnlyList<string> GetChildDirectories(string projectRoot, string folderPath)
    {
        var boundary = new PathBoundary(projectRoot);
        var safeFolder = boundary.EnsureSafe(folderPath);

        return Directory.EnumerateDirectories(safeFolder)
            .Where(boundary.IsSafeExistingPath)
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .OrderBy(path => Path.GetFileName(path) ?? path, NaturalStringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
