using System.Text;
using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed record TextPreviewResult(string? Text, string? UnavailableReason);

public static class TextPreviewReader
{
    public const int MaximumBytes = 1_500_000;

    public static bool IsKnownText(string extension) =>
        extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
        || (FileFormatCatalog.TryGet(extension, out var format)
            && format.VisualKind is FileVisualKind.Code or FileVisualKind.Script or FileVisualKind.Data
                or FileVisualKind.Text or FileVisualKind.Markdown or FileVisualKind.Web);

    public static bool IsCode(string extension) =>
        FileFormatCatalog.TryGet(extension, out var format)
        && format.VisualKind is FileVisualKind.Code or FileVisualKind.Script or FileVisualKind.Data or FileVisualKind.Web;

    public static async Task<TextPreviewResult> ReadAsync(
        string projectRoot, string path, CancellationToken cancellationToken = default)
    {
        var safePath = new PathBoundary(projectRoot).EnsureSafe(path);
        await using var stream = new FileStream(safePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumBytes)
            return new(null, "文件超过 1.5 MB，暂不加载文本预览；可在默认应用中打开。");

        // Read at most the limit plus one, including files growing during the read.
        var bytes = new byte[MaximumBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumBytes)
            return new(null, "文件超过 1.5 MB，暂不加载文本预览；可在默认应用中打开。");

        try
        {
            Encoding encoding = new UTF8Encoding(false, true);
            var offset = 0;
            if (count >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            { encoding = new UTF32Encoding(false, true, true); offset = 4; }
            else if (count >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            { encoding = new UTF32Encoding(true, true, true); offset = 4; }
            else if (count >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            { encoding = new UnicodeEncoding(false, true, true); offset = 2; }
            else if (count >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            { encoding = new UnicodeEncoding(true, true, true); offset = 2; }
            else if (count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                offset = 3;

            var text = encoding.GetString(bytes, offset, count - offset);
            if (text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t' and not '\f'))
                return new(null, "此文件包含二进制内容，无法作为文本预览。");
            return new(text, null);
        }
        catch (DecoderFallbackException)
        {
            return new(null, "此文件不是可识别的 Unicode 文本；可在默认应用中打开。");
        }
    }
}
