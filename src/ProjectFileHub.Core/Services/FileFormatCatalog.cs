using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed record FileFormatDescriptor(
    FileItemCategory Category,
    FileVisualKind VisualKind,
    string Badge,
    string DisplayType);

public static class FileFormatCatalog
{
    private static readonly IReadOnlyDictionary<string, FileFormatDescriptor> Formats = BuildFormats();

    public static int SupportedExtensionCount => Formats.Count;

    public static bool TryGet(string extension, out FileFormatDescriptor descriptor) =>
        Formats.TryGetValue(Normalize(extension), out descriptor!);

    public static string GetDisplayType(FileSystemItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDirectory)
        {
            return "文件夹";
        }

        if (TryGet(item.Extension, out var descriptor))
        {
            return descriptor.DisplayType;
        }

        var compact = CompactExtension(item.Extension);
        return string.IsNullOrEmpty(compact) ? "文件" : $"{compact} 文件";
    }

    public static string GetFallbackBadge(string extension) => CompactExtension(extension);

    private static IReadOnlyDictionary<string, FileFormatDescriptor> BuildFormats()
    {
        var formats = new Dictionary<string, FileFormatDescriptor>(StringComparer.OrdinalIgnoreCase);

        AddPerExtensionWithBadge(formats, FileItemCategory.Image, FileVisualKind.Image, "图片", string.Empty, useExtensionInDisplayType: true,
            ".avif", ".bmp", ".gif", ".heic", ".heif", ".ico", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp");
        AddPerExtension(formats, FileItemCategory.Image, FileVisualKind.CameraRaw, "相机原片",
            ".arw", ".cr2", ".cr3", ".dng", ".nef", ".nrw", ".orf", ".pef", ".raf", ".raw", ".rw2", ".sr2", ".srf", ".x3f");
        AddPerExtension(formats, FileItemCategory.Image, FileVisualKind.VectorEditor, "矢量图片", ".svg");
        AddPerExtension(formats, FileItemCategory.Video, FileVisualKind.Video, "视频",
            ".avi", ".flv", ".m2ts", ".m4v", ".mkv", ".mov", ".mp4", ".mpeg", ".mpg", ".mts", ".webm", ".wmv");
        AddPerExtension(formats, FileItemCategory.Audio, FileVisualKind.Audio, "音频",
            ".aac", ".aif", ".aiff", ".flac", ".m4a", ".mid", ".midi", ".mp3", ".ogg", ".opus", ".wav", ".wma");

        AddFamily(formats, FileItemCategory.Document, FileVisualKind.Pdf, "PDF 文档", null, ".pdf");
        AddFamily(formats, FileItemCategory.Document, FileVisualKind.Word, "Word 文档", null,
            ".doc", ".docx", ".dot", ".dotx", ".odt", ".rtf");
        AddFamily(formats, FileItemCategory.Document, FileVisualKind.Spreadsheet, "电子表格", null,
            ".csv", ".ods", ".xls", ".xlsm", ".xlsx");
        AddFamily(formats, FileItemCategory.Document, FileVisualKind.Presentation, "演示文稿", null,
            ".odp", ".potx", ".ppt", ".pptx");
        AddFamily(formats, FileItemCategory.Document, FileVisualKind.Markdown, "Markdown 文档", "MD",
            ".markdown", ".md", ".mdx");
        AddFamily(formats, FileItemCategory.Document, FileVisualKind.Text, "文本文件", null,
            ".log", ".nfo", ".text", ".txt");
        AddPerExtension(formats, FileItemCategory.Document, FileVisualKind.Ebook, "电子书",
            ".azw3", ".cbr", ".cbz", ".djvu", ".epub", ".fb2", ".mobi");
        AddPerExtension(formats, FileItemCategory.Document, FileVisualKind.Mail, "邮件",
            ".eml", ".msg", ".oft");
        AddPerExtension(formats, FileItemCategory.Code, FileVisualKind.Web, "网页",
            ".htm", ".html", ".mht", ".mhtml", ".url", ".webarchive");

        AddPerExtension(formats, FileItemCategory.Code, FileVisualKind.Data, "数据",
            ".cfg", ".conf", ".env", ".ini", ".json", ".jsonc", ".lock", ".plist", ".properties", ".toml", ".xml", ".yaml", ".yml");
        AddFamily(formats, FileItemCategory.Other, FileVisualKind.Database, "数据库", "DB",
            ".accdb", ".db", ".db3", ".mdb", ".sqlite", ".sqlite3");

        AddPerExtension(formats, FileItemCategory.Code, FileVisualKind.Code, "源代码",
            ".astro", ".c", ".cc", ".cpp", ".cs", ".csx", ".css", ".dart", ".fs", ".fsx", ".go", ".h", ".hpp", ".java", ".js", ".jsx", ".kt", ".kts", ".less", ".lua", ".php", ".py", ".pyw", ".razor", ".rb", ".rs", ".sass", ".scss", ".sql", ".svelte", ".swift", ".ts", ".tsx", ".vue", ".xaml");
        AddPerExtension(formats, FileItemCategory.Code, FileVisualKind.Script, "脚本",
            ".bat", ".cmd", ".ps1", ".reg", ".sh");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.Executable, "应用程序",
            ".appx", ".com", ".exe", ".msi", ".msix");

        AddPerExtension(formats, FileItemCategory.Archive, FileVisualKind.Archive, "压缩包",
            ".7z", ".bz2", ".gz", ".rar", ".tar", ".tgz", ".xz", ".zip", ".zst");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.Font, "字体",
            ".otf", ".ttf", ".woff", ".woff2");

        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.RasterEditor, "位图设计",
            ".afphoto", ".clip", ".csp", ".kra", ".ora", ".psb", ".psd", ".xcf");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.VectorEditor, "矢量设计",
            ".afdesign", ".ai", ".cdr", ".eps");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.UiPrototype, "界面原型",
            ".fig", ".sketch", ".xd");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.MotionGraphics, "动效工程",
            ".aep", ".aet", ".mogrt");
        AddPerExtension(formats, FileItemCategory.Video, FileVisualKind.VideoProject, "剪辑工程",
            ".drp", ".fcpxml", ".kdenlive", ".mlt", ".prproj", ".veg");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.Blender, "三维场景",
            ".blend", ".blend1", ".blend2");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.Mesh3D, "三维模型",
            ".3ds", ".abc", ".dae", ".fbx", ".glb", ".gltf", ".obj", ".ply", ".stl", ".usd", ".usda", ".usdc", ".usdz");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.Cad, "工程制图",
            ".dwf", ".dwg", ".dwt", ".dxf", ".iges", ".igs", ".step", ".stp");
        AddPerExtension(formats, FileItemCategory.Other, FileVisualKind.DesignPackage, "排版设计",
            ".afpub", ".idml", ".indb", ".indd", ".qxp", ".sla");

        AddNamed(formats, ".psd", FileItemCategory.Other, FileVisualKind.RasterEditor, "PSD", "Photoshop 文件");
        AddNamed(formats, ".ai", FileItemCategory.Other, FileVisualKind.VectorEditor, "AI", "Illustrator 文件");
        AddNamed(formats, ".fig", FileItemCategory.Other, FileVisualKind.UiPrototype, "FIG", "Figma 文件");
        AddNamed(formats, ".sketch", FileItemCategory.Other, FileVisualKind.UiPrototype, "SKET", "Sketch 文件");
        AddNamed(formats, ".aep", FileItemCategory.Other, FileVisualKind.MotionGraphics, "AEP", "After Effects 项目");
        AddNamed(formats, ".prproj", FileItemCategory.Video, FileVisualKind.VideoProject, "PR", "Premiere 项目");
        AddNamed(formats, ".blend", FileItemCategory.Other, FileVisualKind.Blender, "BLND", "Blender 文件");

        return formats;
    }

    private static void AddPerExtension(
        IDictionary<string, FileFormatDescriptor> formats,
        FileItemCategory category,
        FileVisualKind visualKind,
        string displaySuffix,
        params string[] extensions) =>
        AddPerExtensionWithBadge(formats, category, visualKind, displaySuffix, badge: null, useExtensionInDisplayType: false, extensions);

    private static void AddPerExtensionWithBadge(
        IDictionary<string, FileFormatDescriptor> formats,
        FileItemCategory category,
        FileVisualKind visualKind,
        string displaySuffix,
        string? badge,
        bool useExtensionInDisplayType,
        params string[] extensions)
    {
        foreach (var extension in extensions)
        {
            var normalized = Normalize(extension);
            var extensionBadge = badge ?? CompactExtension(normalized);
            var displayLabel = useExtensionInDisplayType
                ? CompactExtension(normalized)
                : extensionBadge;
            var displayType = string.IsNullOrEmpty(displayLabel)
                ? displaySuffix
                : $"{displayLabel} {displaySuffix}";
            formats[normalized] = new FileFormatDescriptor(category, visualKind, extensionBadge, displayType);
        }
    }

    private static void AddFamily(
        IDictionary<string, FileFormatDescriptor> formats,
        FileItemCategory category,
        FileVisualKind visualKind,
        string displayType,
        string? badge,
        params string[] extensions)
    {
        foreach (var extension in extensions)
        {
            var normalized = Normalize(extension);
            formats[normalized] = new FileFormatDescriptor(
                category,
                visualKind,
                badge ?? CompactExtension(normalized),
                displayType);
        }
    }

    private static void AddNamed(
        IDictionary<string, FileFormatDescriptor> formats,
        string extension,
        FileItemCategory category,
        FileVisualKind visualKind,
        string badge,
        string displayType) =>
        formats[Normalize(extension)] = new FileFormatDescriptor(
            category,
            visualKind,
            badge,
            displayType);

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
