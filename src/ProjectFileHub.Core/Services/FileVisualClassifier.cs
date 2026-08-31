using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public static class FileVisualClassifier
{
    public static FileVisualKind Classify(FileSystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDirectory)
        {
            return FileVisualKind.Folder;
        }

        if (FileFormatCatalog.TryGet(item.Extension, out var descriptor))
        {
            return descriptor.VisualKind;
        }

        if (item.Category == FileItemCategory.Image) return FileVisualKind.Image;
        if (item.Category == FileItemCategory.Video) return FileVisualKind.Video;
        if (item.Category == FileItemCategory.Audio) return FileVisualKind.Audio;
        if (item.Category == FileItemCategory.Code) return FileVisualKind.Code;
        if (item.Category == FileItemCategory.Archive) return FileVisualKind.Archive;
        if (item.Category == FileItemCategory.Document) return FileVisualKind.Document;
        return FileVisualKind.Other;
    }

    public static string GetBadge(FileSystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var kind = Classify(item);
        if (kind == FileVisualKind.Folder)
        {
            return "DIR";
        }

        if (kind == FileVisualKind.Image)
        {
            return string.Empty;
        }

        if (FileFormatCatalog.TryGet(item.Extension, out var descriptor))
        {
            return descriptor.Badge;
        }

        return FileFormatCatalog.GetFallbackBadge(item.Extension);
    }

    public static string GetTypeMonogram(FileSystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDirectory)
        {
            return "DIR";
        }

        if (FileFormatCatalog.TryGet(item.Extension, out var descriptor))
        {
            var badge = string.IsNullOrWhiteSpace(descriptor.Badge)
                ? FileFormatCatalog.GetFallbackBadge(item.Extension)
                : descriptor.Badge;
            return string.IsNullOrWhiteSpace(badge) ? "FILE" : badge;
        }

        var fallback = FileFormatCatalog.GetFallbackBadge(item.Extension);
        return string.IsNullOrWhiteSpace(fallback) ? "FILE" : fallback;
    }
}
