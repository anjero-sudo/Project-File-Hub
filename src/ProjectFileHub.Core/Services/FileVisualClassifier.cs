using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public static class FileVisualClassifier
{
    private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".odt", ".rtf"
    };

    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".ods", ".xls", ".xlsm", ".xlsx"
    };

    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".odp", ".ppt", ".pptx"
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".markdown", ".md", ".mdx"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".nfo", ".text", ".txt"
    };

    private static readonly HashSet<string> DataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini", ".json", ".toml", ".xml", ".yaml", ".yml"
    };

    private static readonly HashSet<string> DatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".db", ".db3", ".sqlite", ".sqlite3"
    };

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appx", ".bat", ".cmd", ".com", ".exe", ".msi", ".msix"
    };

    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".otf", ".ttf", ".woff", ".woff2"
    };

    public static FileVisualKind Classify(FileSystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDirectory)
        {
            return FileVisualKind.Folder;
        }

        var extension = Normalize(item.Extension);
        if (item.Category == FileItemCategory.Image) return FileVisualKind.Image;
        if (item.Category == FileItemCategory.Video) return FileVisualKind.Video;
        if (item.Category == FileItemCategory.Audio) return FileVisualKind.Audio;
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)) return FileVisualKind.Pdf;
        if (WordExtensions.Contains(extension)) return FileVisualKind.Word;
        if (SpreadsheetExtensions.Contains(extension)) return FileVisualKind.Spreadsheet;
        if (PresentationExtensions.Contains(extension)) return FileVisualKind.Presentation;
        if (MarkdownExtensions.Contains(extension)) return FileVisualKind.Markdown;
        if (TextExtensions.Contains(extension)) return FileVisualKind.Text;
        if (DatabaseExtensions.Contains(extension)) return FileVisualKind.Database;
        if (DataExtensions.Contains(extension)) return FileVisualKind.Data;
        if (item.Category == FileItemCategory.Code) return FileVisualKind.Code;
        if (item.Category == FileItemCategory.Archive) return FileVisualKind.Archive;
        if (ExecutableExtensions.Contains(extension)) return FileVisualKind.Executable;
        if (FontExtensions.Contains(extension)) return FileVisualKind.Font;
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

        return kind switch
        {
            FileVisualKind.Pdf => "PDF",
            FileVisualKind.Word => "DOC",
            FileVisualKind.Spreadsheet => Normalize(item.Extension) == ".csv" ? "CSV" : "XLS",
            FileVisualKind.Presentation => "PPT",
            FileVisualKind.Markdown => "MD",
            FileVisualKind.Text => "TXT",
            FileVisualKind.Database => "DB",
            _ => CompactExtension(item.Extension)
        };
    }

    private static string CompactExtension(string extension)
    {
        var label = Normalize(extension).TrimStart('.').ToUpperInvariant();
        return label.Length <= 4 ? label : label[..4];
    }

    private static string Normalize(string extension) =>
        string.IsNullOrWhiteSpace(extension)
            ? string.Empty
            : extension.StartsWith('.') ? extension : $".{extension}";
}
