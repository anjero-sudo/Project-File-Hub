using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed record FileIconDescriptor(string Glyph, string AccessibleName);

public static class FileIconCatalog
{
    private static readonly IReadOnlyDictionary<FileVisualKind, FileIconDescriptor> Icons =
        new Dictionary<FileVisualKind, FileIconDescriptor>
        {
            [FileVisualKind.Folder] = Icon('\uE8B7', "文件夹"),
            [FileVisualKind.Image] = Icon('\uEB9F', "图片"),
            [FileVisualKind.Video] = Icon('\uE714', "视频"),
            [FileVisualKind.Audio] = Icon('\uE8D6', "音频"),
            [FileVisualKind.Pdf] = Icon('\uEA90', "PDF 文档"),
            [FileVisualKind.Word] = Icon('\uE9F9', "文字处理文档"),
            [FileVisualKind.Spreadsheet] = Icon('\uF0E2', "电子表格"),
            [FileVisualKind.Presentation] = Icon('\uE95D', "演示文稿"),
            [FileVisualKind.Markdown] = Icon('\uF000', "Markdown 文档"),
            [FileVisualKind.Text] = Icon('\uEF60', "文本文件"),
            [FileVisualKind.Code] = Icon('\uE943', "源代码"),
            [FileVisualKind.Data] = Icon('\uEA37', "结构化数据或配置"),
            [FileVisualKind.Database] = Icon('\uE965', "数据库"),
            [FileVisualKind.Archive] = Icon('\uF012', "压缩文件"),
            [FileVisualKind.Script] = Icon('\uE756', "命令或脚本"),
            [FileVisualKind.Executable] = Icon('\uEB3B', "应用程序"),
            [FileVisualKind.Font] = Icon('\uE8D2', "字体"),
            [FileVisualKind.Web] = Icon('\uEB41', "网页"),
            [FileVisualKind.Mail] = Icon('\uE715', "邮件"),
            [FileVisualKind.Ebook] = Icon('\uE82D', "电子书"),
            [FileVisualKind.RasterEditor] = Icon('\uE790', "位图设计文件"),
            [FileVisualKind.VectorEditor] = Icon('\uEDFB', "矢量设计文件"),
            [FileVisualKind.UiPrototype] = Icon('\uEB3C', "界面原型文件"),
            [FileVisualKind.MotionGraphics] = Icon('\uE794', "动效工程"),
            [FileVisualKind.VideoProject] = Icon('\uE8B2', "视频剪辑工程"),
            [FileVisualKind.Blender] = Icon('\uE81E', "三维场景工程"),
            [FileVisualKind.Mesh3D] = Icon('\uE914', "三维模型"),
            [FileVisualKind.Cad] = Icon('\uEC87', "工程制图文件"),
            [FileVisualKind.CameraRaw] = Icon('\uE722', "相机原片"),
            [FileVisualKind.DesignPackage] = Icon('\uE7B8', "排版设计文件"),
            [FileVisualKind.Document] = Icon('\uE8A5', "文档"),
            [FileVisualKind.Other] = Icon('\uE7C3', "文件")
        };

    public static int IconFamilyCount => Icons.Count;

    public static int DistinctGlyphCount =>
        Icons.Values.Select(icon => icon.Glyph).Distinct(StringComparer.Ordinal).Count();

    public static FileIconDescriptor Get(FileVisualKind kind) =>
        Icons.TryGetValue(kind, out var descriptor)
            ? descriptor
            : Icons[FileVisualKind.Other];

    private static FileIconDescriptor Icon(char glyph, string accessibleName) =>
        new(glyph.ToString(), accessibleName);
}
