using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed class FileSystemBrowser
{
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

        var comparer = NaturalStringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<FileSystemItem> ordered = options.SortField switch
        {
            FileSortField.ModifiedAt => items.OrderBy(item => item.ModifiedAt),
            FileSortField.CreatedAt => items.OrderBy(item => item.CreatedAt),
            FileSortField.Type => items.OrderBy(item => item.DisplayType, comparer),
            FileSortField.Size => items.OrderBy(item => item.Size ?? -1),
            _ => items.OrderBy(item => item.Name, comparer)
        };

        ordered = ordered.ThenBy(item => item.Name, comparer);

        var sorted = options.Direction == SortDirection.Descending
            ? ordered.Reverse().ToArray()
            : ordered.ToArray();

        cancellationToken.ThrowIfCancellationRequested();

        // Directories stay grouped first without changing the selected field inside each group.
        return sorted.OrderByDescending(item => item.IsDirectory).ToArray();
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
