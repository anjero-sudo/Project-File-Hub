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
        if (kind is FileVisualKind.Folder or FileVisualKind.Image)
        {
            return string.Empty;
        }

        if (FileFormatCatalog.TryGet(item.Extension, out var descriptor))
        {
            return descriptor.Badge;
        }

        return FileFormatCatalog.GetFallbackBadge(item.Extension);
    }
}
