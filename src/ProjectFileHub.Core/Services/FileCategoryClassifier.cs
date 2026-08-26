using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public static class FileCategoryClassifier
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avi", ".m4v", ".mkv", ".mov", ".mp4", ".webm", ".wmv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac", ".flac", ".m4a", ".mp3", ".ogg", ".wav", ".wma"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".doc", ".docx", ".md", ".pdf", ".ppt", ".pptx", ".rtf", ".txt", ".xls", ".xlsx"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cpp", ".cs", ".css", ".go", ".h", ".hpp", ".html", ".java", ".js", ".json", ".jsx",
        ".kt", ".lua", ".php", ".ps1", ".py", ".rb", ".rs", ".sql", ".swift", ".ts", ".tsx", ".xml", ".xaml", ".yaml", ".yml"
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".bz2", ".gz", ".rar", ".tar", ".xz", ".zip"
    };

    public static FileItemCategory Classify(string extension, bool isDirectory)
    {
        if (isDirectory)
        {
            return FileItemCategory.Folder;
        }

        if (ImageExtensions.Contains(extension)) return FileItemCategory.Image;
        if (VideoExtensions.Contains(extension)) return FileItemCategory.Video;
        if (AudioExtensions.Contains(extension)) return FileItemCategory.Audio;
        if (DocumentExtensions.Contains(extension)) return FileItemCategory.Document;
        if (CodeExtensions.Contains(extension)) return FileItemCategory.Code;
        if (ArchiveExtensions.Contains(extension)) return FileItemCategory.Archive;
        return FileItemCategory.Other;
    }
}
