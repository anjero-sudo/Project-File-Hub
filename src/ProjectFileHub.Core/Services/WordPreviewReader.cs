using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using OpenMcdf;

namespace ProjectFileHub.Core.Services;

public sealed record WordPreviewResult(string Text, bool IsTruncated)
{
    public string Notice => "已保存内容预览 · 仅正文和表格文字，不含图片、页眉页脚及原始排版"
        + (IsTruncated ? " · 内容较长，仅显示前 20 万字符" : string.Empty);
}

/// <summary>Read-only, bounded Word text extraction. Never activates Office, macros or linked content.</summary>
public static class WordPreviewReader
{
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumCharacters = 200_000;
    private const int MaximumXmlBytes = 8 * 1024 * 1024;
    private const int MaximumClxBytes = 4 * 1024 * 1024;
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string StrictWordNamespace = "http://purl.oclc.org/ooxml/wordprocessingml/main";

    public static bool Supports(string extension) => extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public static bool IsTemporaryFile(string path) => Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    public const string TemporaryFileMessage = "这是 Word 自动生成的临时锁文件，不包含文档正文。\n请选择同一文件夹中名称不以 ~$ 开头的原始文档。";

    public static Task<WordPreviewResult> ReadAsync(string projectRoot, string path, CancellationToken token = default) =>
        Task.Run(() => Read(projectRoot, path, token), token);

    private static WordPreviewResult Read(string projectRoot, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var safePath = new PathBoundary(projectRoot).EnsureSafe(path);
        if (!Supports(Path.GetExtension(safePath))) throw new NotSupportedException("仅支持 DOC 和 DOCX 文档。");
        if (IsTemporaryFile(safePath)) throw new NotSupportedException(TemporaryFileMessage);
        // Allow Word's existing writer handle, then parse a bounded in-memory snapshot.
        // Closing the source before parsing also avoids keeping Word's save/replace operation open.
        using var stream = ReadSnapshot(safePath, token);
        Span<byte> signature = stackalloc byte[8];
        if (stream.Read(signature) < 8) throw new InvalidDataException("文档为空或格式不完整。");
        stream.Position = 0;
        if (signature[0] == 'P' && signature[1] == 'K') return ReadDocx(stream, token);
        if (signature.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            return ReadDoc(stream, token);
        throw new NotSupportedException("文件内容不是可识别的 DOC 或 DOCX 格式。请在 Word 中另存为标准 DOCX 后重试。");
    }

    private static MemoryStream ReadSnapshot(string path, CancellationToken token)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var expectedLength = source.Length;
        if (expectedLength > MaximumFileBytes) throw new InvalidDataException("文档超过 64 MB，请使用默认应用打开。");
        var snapshot = new MemoryStream((int)expectedLength);
        try
        {
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = source.Read(buffer)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (snapshot.Length + count > MaximumFileBytes)
                    throw new InvalidDataException("文档超过 64 MB，请使用默认应用打开。");
                snapshot.Write(buffer, 0, count);
            }
            if (snapshot.Length != expectedLength || source.Length != expectedLength)
                throw new IOException("文档正在保存，请稍后重新预览。");
            snapshot.Position = 0;
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    private static WordPreviewResult ReadDocx(Stream file, CancellationToken token)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("文档缺少正文，或不是有效的 DOCX 文件。");
        if (entry.Length > MaximumXmlBytes) throw new InvalidDataException("文档正文结构过大，请使用默认应用打开。");
        using var xmlStream = entry.Open();
        using var reader = XmlReader.Create(xmlStream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlBytes,
            IgnoreComments = true
        });
        var text = new StringBuilder();
        var bodyDepth = -1;
        var textDepth = -1;
        var ignoredDepth = -1;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.Depth > 128) throw new InvalidDataException("文档嵌套层级异常，无法预览。");
            if (ignoredDepth >= 0)
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == ignoredDepth) ignoredDepth = -1;
                continue;
            }
            var isWord = reader.NamespaceURI is WordNamespace or StrictWordNamespace;
            if (isWord && reader.NodeType == XmlNodeType.Element)
            {
                if (reader.LocalName == "body") bodyDepth = reader.Depth;
                if (bodyDepth < 0) continue;
                switch (reader.LocalName)
                {
                    case "del": case "moveFrom": case "drawing": case "pict": case "object":
                        if (!reader.IsEmptyElement) ignoredDepth = reader.Depth;
                        break;
                    case "t": textDepth = reader.IsEmptyElement ? -1 : reader.Depth; break;
                    case "tab": text.Append('\t'); break;
                    case "br": case "cr": text.Append('\n'); break;
                    case "noBreakHyphen": text.Append('\u2011'); break;
                    case "softHyphen": text.Append('\u00ad'); break;
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace && textDepth >= 0)
            {
                var room = MaximumCharacters + 1 - text.Length;
                text.Append(reader.Value.AsSpan(0, Math.Min(reader.Value.Length, Math.Max(0, room))));
            }
            else if (isWord && reader.NodeType == XmlNodeType.EndElement && bodyDepth >= 0)
            {
                switch (reader.LocalName)
                {
                    case "t": textDepth = -1; break;
                    case "p": text.Append('\n'); break;
                    case "tc":
                        while (text.Length > 0 && text[^1] == '\n') text.Length--;
                        text.Append('\t'); break;
                    case "tr":
                        if (text.Length > 0 && text[^1] == '\t') text.Length--;
                        text.Append('\n'); break;
                    case "body": return Finish(text, false);
                }
            }
            if (text.Length > MaximumCharacters) return Finish(text, true);
        }
        if (bodyDepth < 0) throw new InvalidDataException("未找到可预览的 Word 正文。");
        return Finish(text, false);
    }

    // MS-DOC 2.4.1: FIB -> FibRgFcLcb97.fcClx/lcbClx -> PlcPcd -> WordDocument text pieces.
    // https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-doc/01d5d8c4-cf9c-4ef9-80fd-439e763cfe01
    private static WordPreviewResult ReadDoc(Stream file, CancellationToken token)
    {
        using var storage = RootStorage.Open(file);
        if (!storage.TryOpenStream("WordDocument", out var word))
            throw new NotSupportedException("文档可能已加密，或不是可识别的 DOC 文件；请使用默认应用打开。");
        using (word)
        {
            var header = ReadBytes(word, 0, 32);
            if (U16(header, 0) != 0xA5EC || U16(header, 2) < 0xC1)
                throw new NotSupportedException("仅支持 Word 97 及更新版本的 DOC 文档。");
            var flags = U16(header, 10);
            if ((flags & 0x8100) != 0) throw new NotSupportedException("文档已加密或受密码保护，请使用默认应用打开。");
            var csw = U16(ReadBytes(word, 32, 2), 0);
            var lwCountOffset = 34 + csw * 2;
            var cslw = U16(ReadBytes(word, lwCountOffset, 2), 0);
            if (cslw < 4) throw new InvalidDataException("DOC 正文索引不完整。");
            var mainCharacters = U32(ReadBytes(word, lwCountOffset + 2 + 12, 4), 0);
            var pairCountOffset = lwCountOffset + 2 + cslw * 4;
            var pairCount = U16(ReadBytes(word, pairCountOffset, 2), 0);
            if (pairCount <= 33) throw new InvalidDataException("DOC 文本片段表缺失。");
            var pair = ReadBytes(word, pairCountOffset + 2 + 33 * 8, 8);
            var clxOffset = U32(pair, 0);
            var clxSize = U32(pair, 4);
            if (clxSize == 0 && mainCharacters == 0) return new(string.Empty, false);
            if (clxSize > MaximumClxBytes) throw new InvalidDataException("DOC 文本片段表过大。");
            using var table = storage.OpenStream((flags & 0x200) != 0 ? "1Table" : "0Table");
            var clx = ReadBytes(table, clxOffset, checked((int)clxSize));
            var offset = 0;
            while (offset < clx.Length && clx[offset] == 1)
            {
                token.ThrowIfCancellationRequested();
                offset = checked(offset + 3 + U16(clx, offset + 1));
            }
            if (offset >= clx.Length || clx[offset] != 2) throw new InvalidDataException("DOC 文本片段表损坏。");
            var plcSize = checked((int)U32(clx, offset + 1));
            offset += 5;
            if (plcSize < 4 || (plcSize - 4) % 12 != 0 || plcSize > clx.Length - offset)
                throw new InvalidDataException("DOC 文本片段长度无效。");
            var pieces = (plcSize - 4) / 12;
            var text = new StringBuilder();
            var fieldInstructions = new Stack<bool>();
            uint previousEnd = 0;
            var truncated = mainCharacters > MaximumCharacters;
            for (var i = 0; i < pieces; i++)
            {
                token.ThrowIfCancellationRequested();
                var start = U32(clx, offset + i * 4);
                var end = U32(clx, offset + (i + 1) * 4);
                if (start != previousEnd || end < start) throw new InvalidDataException("DOC 文本片段顺序无效。");
                previousEnd = end;
                if (start >= mainCharacters || start >= MaximumCharacters) break;
                var count = checked((int)(Math.Min(Math.Min(end, mainCharacters), MaximumCharacters) - start));
                var packed = U32(clx, offset + (pieces + 1) * 4 + i * 8 + 2);
                var compressed = (packed & 0x40000000) != 0;
                var position = packed & 0x3FFFFFFF;
                var bytes = ReadBytes(word, compressed ? position / 2 : position, count * (compressed ? 1 : 2));
                var segment = compressed ? DecodeCompressed(bytes) : Encoding.Unicode.GetString(bytes);
                foreach (var c in segment)
                {
                    if (c == '\x13') { if (fieldInstructions.Count >= 64) throw new InvalidDataException("DOC 域嵌套层级异常。"); fieldInstructions.Push(true); continue; }
                    if (c == '\x14' && fieldInstructions.Count > 0) { fieldInstructions.Pop(); fieldInstructions.Push(false); continue; }
                    if (c == '\x15' && fieldInstructions.Count > 0) { fieldInstructions.Pop(); continue; }
                    if (fieldInstructions.Contains(true)) continue;
                    if (c is '\r' or '\x0b' or '\x0c') text.Append('\n');
                    else if (c == '\x07') text.Append('\t');
                    else if (!char.IsControl(c) || c is '\n' or '\t') text.Append(c);
                }
            }
            if (previousEnd < Math.Min(mainCharacters, MaximumCharacters)) throw new InvalidDataException("DOC 正文片段不完整。");
            return Finish(text, truncated);
        }
    }

    private static byte[] ReadBytes(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || count > MaximumClxBytes || offset > stream.Length - count)
            throw new InvalidDataException("文档中的数据范围无效。");
        stream.Position = offset;
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static ushort U16(byte[] bytes, int offset) => offset >= 0 && offset <= bytes.Length - 2
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)) : throw new InvalidDataException("文档数据不完整。");
    private static uint U32(byte[] bytes, int offset) => offset >= 0 && offset <= bytes.Length - 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) : throw new InvalidDataException("文档数据不完整。");

    private static string DecodeCompressed(byte[] bytes)
    {
        // MS-DOC FcCompressed mappings differ from Windows-1252 at 0x80/0x8E/0x9E.
        const string mapping = "\u0080\u0081\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u008D\u008E\u008F\u0090\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u009D\u009E\u0178";
        return new string(bytes.Select(b => b is >= 0x80 and <= 0x9F ? mapping[b - 0x80] : (char)b).ToArray());
    }

    private static WordPreviewResult Finish(StringBuilder text, bool truncated)
    {
        if (text.Length > MaximumCharacters) { text.Length = MaximumCharacters; truncated = true; }
        return new(text.ToString().TrimEnd('\n', '\r', '\t'), truncated);
    }
}
