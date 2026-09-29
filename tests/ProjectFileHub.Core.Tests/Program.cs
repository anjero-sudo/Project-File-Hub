using System.Buffers.Binary;
using System.IO.Compression;
using RootStorage = OpenMcdf.RootStorage;
using System.Net;
using System.Text;
using ProjectFileHub.Core;
using ProjectFileHub.Core.Models;
using ProjectFileHub.Core.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("DOCX preview reads Chinese paragraphs and table cells without loading links", TestDocxPreview),
    ("DOC preview reads Unicode and compressed pieces and excludes field instructions", TestDocPreview),
    ("Word preview rejects corrupt, encrypted, oversized and escaping documents", TestWordPreviewGuards),
    ("Word preview reads saved content while an editor is open and identifies owner files", TestWordOpenDocument),
    ("Filename initials search matches Chinese folders and mixed filenames", TestPinyinSearch),
    ("Text preview accepts source and unknown Unicode but rejects binary, oversized and escaping paths", TestTextPreviewReader),
    ("Folder name search composes with type filters and parent navigation stops at root", TestSearchAndParent),
    ("Project aliases persist without changing roots or active identity", TestProjectAlias),
    ("Path boundary accepts the root and descendants", TestBoundaryAcceptsRootAndDescendants),
    ("Path boundary rejects prefix siblings and traversal", TestBoundaryRejectsEscapes),
    ("Natural sort orders numeric filename segments", TestNaturalSort),
    ("File visuals distinguish common extensions and keep images badge-free", TestFileVisuals),
    ("File browser filters categories and keeps folders first", TestFileBrowser),
    ("File browser filtering stays in the selected folder and reports progress", TestFileBrowserScopeAndProgress),
    ("Project registry preserves one active project without deleting roots", TestProjectRegistry),
    ("App settings persist workspace memory and normalize display choices", TestAppSettingsStore),
    ("Rename validates names, conflicts and the project root", TestRename),
    ("Transfer moves and copies only inside the project", TestTransfer),
    ("Batch copy preserves all selections and keep-both paste", TestBatchCopy),
    ("Markdown preview parses reading structure without resolving content", TestMarkdownPreviewParser),
    ("Markdown HTML preview encodes source and exposes safe reading interactions", TestMarkdownHtmlRenderer),
    ("Markdown tables preserve rows, alignment, escaped pipes and safe cell markup", TestMarkdownTables),
    ("Markdown project links resolve relative files without escaping the project", TestMarkdownProjectLinkResolver),
    ("GitHub release checks compare versions without accepting untrusted links", TestGitHubReleaseUpdateService),
    ("Code preview tokenization preserves text and identifies Monokai token roles", TestCodePreviewTokenizer),
    ("Image preview zoom keeps the viewport center and clamps mouse panning", TestPreviewZoomMath),
    ("Transfer blocks self, subtree and nested selections", TestTransferGuards),
    ("Conflict policies keep both, replace files and skip", TestConflictPolicies),
    ("External import copies files and folders into the project", TestExternalImport),
    ("Recycle planning protects the project root and nested selections", TestRecyclePlanning),
    ("Bounded cache enforces LRU entry and byte budgets", TestBoundedCacheBudgets),
    ("Bounded cache single-flight survives one canceled waiter", TestBoundedCacheSingleFlight),
    ("Bounded cache invalidation rejects stale results", TestBoundedCacheInvalidation),
    ("Bounded cache timeout keeps native work in its slot and caps the queue", TestBoundedCacheTimeoutAndQueue),
    ("SQLite index search and sort match direct folder browsing", TestProjectIndexSearchAndSort),
    ("SQLite project index scans subfolders and tracks new files", TestProjectIndex)
};

if (args.Length == 3 && args[0] == "--markdown-preview")
{
    var markdown = await File.ReadAllTextAsync(args[1]);
    await File.WriteAllTextAsync(args[2], MarkdownHtmlRenderer.Render(markdown, MarkdownHtmlTheme.WarmGraphite, wrapCodeBlocks: true));
    var tables = MarkdownPreviewParser.Parse(markdown).Where(block => block.Table is not null).Select(block => block.Table!).ToArray();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { tables = tables.Length, rows = tables.Sum(table => table.Rows.Count), columns = tables.Select(table => table.Headers.Count).ToArray() }));
    return 0;
}

if (args.Length == 2 && args[0] == "--word-fixtures")
{
    var root = Path.GetFullPath(args[1]);
    foreach (var file in Directory.EnumerateFiles(root).Where(path => WordPreviewReader.Supports(Path.GetExtension(path))))
    {
        try
        {
            var result = await WordPreviewReader.ReadAsync(root, file);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { file = Path.GetFileName(file), result.Text, result.IsTruncated }));
        }
        catch (Exception exception)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { file = Path.GetFileName(file), error = exception.Message }));
        }
    }
    return 0;
}

var failures = new List<string>();

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add(test.Name);
        Console.Error.WriteLine($"FAIL  {test.Name}\n      {exception}");
    }
}

Console.WriteLine($"\n{tests.Length - failures.Count}/{tests.Length} tests passed.");
return failures.Count == 0 ? 0 : 1;

static void CreateDocxFixture(string path, string body)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
    writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + body + "</w:body></w:document>");
}

static async Task TestDocxPreview()
{
    using var workspace = TemporaryWorkspace.Create();
    var path = Path.Combine(workspace.Root, "正文.docx");
    CreateDocxFixture(path, """
        <w:p><w:r><w:t>中文标题</w:t></w:r></w:p>
        <w:p><w:r><w:t xml:space="preserve">第一段 &lt;script&gt; 保留文字 </w:t><w:tab/><w:t>结尾</w:t></w:r></w:p>
        <w:tbl><w:tr><w:tc><w:p><w:r><w:t>姓名</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>分数</w:t></w:r></w:p></w:tc></w:tr>
        <w:tr><w:tc><w:p><w:r><w:t>小明</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>98</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
        <w:p><w:del><w:r><w:delText>已删除</w:delText></w:r></w:del><w:r><w:instrText>INCLUDETEXT secret</w:instrText><w:t>最后一段</w:t></w:r></w:p>
        """);
    var original = await File.ReadAllBytesAsync(path);
    var result = await WordPreviewReader.ReadAsync(workspace.Root, path);
    Assert(result.Text == "中文标题\n第一段 <script> 保留文字 \t结尾\n姓名\t分数\n小明\t98\n最后一段", "Paragraph and table reading order must be preserved; deleted text and field instructions must be excluded.");
    Assert(!result.IsTruncated, "Small document must be complete.");
    var afterPreview = await File.ReadAllBytesAsync(path);
    Assert(original.SequenceEqual(afterPreview), "Preview must not modify the document.");
    var longPath = Path.Combine(workspace.Root, "long.docx");
    CreateDocxFixture(longPath, "<w:p><w:r><w:t>" + new string('中', WordPreviewReader.MaximumCharacters + 10) + "</w:t></w:r></w:p>");
    var longResult = await WordPreviewReader.ReadAsync(workspace.Root, longPath);
    Assert(longResult.IsTruncated && longResult.Text.Length == WordPreviewReader.MaximumCharacters, "Long documents must have bounded output and an explicit truncation notice.");
}

static void CreateDocFixture(string path, string first, string second, bool encrypted = false, bool invalidPiece = false)
{
    var firstBytes = Encoding.Latin1.GetBytes(first);
    var secondBytes = Encoding.Unicode.GetBytes(second);
    var word = new byte[1024 + firstBytes.Length + secondBytes.Length];
    void W16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(word.AsSpan(offset), value);
    void W32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(word.AsSpan(offset), value);
    W16(0, 0xA5EC); W16(2, 0xC1); W16(10, (ushort)(0x200 | (encrypted ? 0x100 : 0)));
    W16(32, 14); W16(62, 22); W32(76, (uint)(first.Length + second.Length)); W16(152, 93);
    // FibRgFcLcb97 pair 33, CLX contains a Pcdt with two pieces.
    W32(418, 0); W32(422, 33);
    firstBytes.CopyTo(word, 1024); secondBytes.CopyTo(word, 1024 + firstBytes.Length);
    var table = new byte[33];
    table[0] = 2;
    void T32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(offset), value);
    T32(1, 28); T32(5, 0); T32(9, (uint)first.Length); T32(13, (uint)(first.Length + second.Length));
    T32(19, 0x40000000 | 2048u); T32(27, invalidPiece ? uint.MaxValue : (uint)(1024 + firstBytes.Length));
    using var root = RootStorage.Create(path);
    using (var wordStream = root.CreateStream("WordDocument")) wordStream.Write(word);
    using (var tableStream = root.CreateStream("1Table")) tableStream.Write(table);
    root.Flush();
}

static async Task TestDocPreview()
{
    using var workspace = TemporaryWorkspace.Create();
    var path = Path.Combine(workspace.Root, "旧文档.doc");
    CreateDocFixture(path, "Title\rA\x07" + "B\r", "中文正文\r\x13" + "HYPERLINK secret\x14显示结果\x15\r第二段");
    var original = await File.ReadAllBytesAsync(path);
    var result = await WordPreviewReader.ReadAsync(workspace.Root, path);
    Assert(result.Text == "Title\nA\tB\n中文正文\n显示结果\n第二段", "DOC pieces must preserve Chinese, paragraphs, cells and field display results.");
    var afterPreview = await File.ReadAllBytesAsync(path);
    Assert(original.SequenceEqual(afterPreview), "Legacy DOC preview must be read-only.");
}

static async Task TestWordOpenDocument()
{
    using var workspace = TemporaryWorkspace.Create();
    var path = Path.Combine(workspace.Root, "正在编辑.docx");
    CreateDocxFixture(path, "<w:p><w:r><w:t>已经保存的正文</w:t></w:r></w:p>");
    var original = await File.ReadAllBytesAsync(path);
    using (var editor = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
    {
        var result = await WordPreviewReader.ReadAsync(workspace.Root, path);
        Assert(result.Text == "已经保存的正文", "An existing editor writer handle must not block read-only saved-content preview.");
    }
    var afterPreview = await File.ReadAllBytesAsync(path);
    Assert(original.SequenceEqual(afterPreview), "Open-document preview must not alter the source bytes.");
    var doc = Path.Combine(workspace.Root, "正在编辑.doc");
    CreateDocFixture(doc, "Title\r", "已经保存的旧文档");
    using (var editor = new FileStream(doc, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        Assert((await WordPreviewReader.ReadAsync(workspace.Root, doc)).Text.Contains("已经保存的旧文档"), "DOC snapshots must also support open editors.");
    var owner = Path.Combine(workspace.Root, "~$正在编辑.docx");
    await File.WriteAllBytesAsync(owner, new byte[162]);
    var ownerRejected = false;
    try { await WordPreviewReader.ReadAsync(workspace.Root, owner); }
    catch (NotSupportedException ex) { ownerRejected = ex.Message == WordPreviewReader.TemporaryFileMessage; }
    Assert(ownerRejected, "Word owner files must receive a precise temporary-file explanation.");
    using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var rejected = false;
        try { await WordPreviewReader.ReadAsync(workspace.Root, path); }
        catch (IOException) { rejected = true; }
        Assert(rejected, "A genuinely exclusive writer must remain protected.");
    }
}

static async Task TestWordPreviewGuards()
{
    using var workspace = TemporaryWorkspace.Create();
    async Task Reject(string path)
    {
        var rejected = false;
        try { await WordPreviewReader.ReadAsync(workspace.Root, path); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or System.Xml.XmlException)
        { rejected = true; }
        Assert(rejected, $"Unsafe/unsupported document must be rejected: {path}");
    }
    var encrypted = Path.Combine(workspace.Root, "encrypted.doc");
    CreateDocFixture(encrypted, "a", "中文", encrypted: true);
    await Reject(encrypted);
    var corrupt = Path.Combine(workspace.Root, "corrupt.doc");
    CreateDocFixture(corrupt, "a", "中文", invalidPiece: true);
    await Reject(corrupt);
    var malformed = Path.Combine(workspace.Root, "malformed.docx");
    await File.WriteAllTextAsync(malformed, "not a word document");
    await Reject(malformed);
    var entity = Path.Combine(workspace.Root, "entity.docx");
    using (var zip = ZipFile.Open(entity, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
        writer.Write("<!DOCTYPE root [<!ENTITY e SYSTEM 'file:///C:/secret'>]><root>&e;</root>");
    await Reject(entity);
    var oversized = Path.Combine(workspace.Root, "large.doc");
    using (var file = File.Create(oversized)) file.SetLength(WordPreviewReader.MaximumFileBytes + 1L);
    await Reject(oversized);
    await Reject(Path.Combine(workspace.Root, "..", "outside.docx"));
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    var cancelled = false;
    try { await WordPreviewReader.ReadAsync(workspace.Root, encrypted, cancel.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Assert(cancelled, "Canceled Word preview must not keep parsing.");
}

static Task TestPinyinSearch()
{
    Assert(FileNameSearch.Matches("人物素材", "rw"), "Folder initials must match.");
    Assert(FileNameSearch.Matches("项目总结.docx", "XMZJ"), "Initials must be case-insensitive.");
    Assert(FileNameSearch.Matches("EP01_人物设定_v2.doc", "rwSD"), "Mixed names must support initials substrings.");
    Assert(FileNameSearch.Matches("人物设定.docx", "rwsd.docx"), "Extension suffix must remain searchable with initials.");
    Assert(FileNameSearch.Matches("人物设定", "人物") && FileNameSearch.Matches("Hero.ASTRO", ".astro"), "Literal search must remain unchanged.");
    Assert(!FileNameSearch.Matches("人物素材", "zzzz"), "Unrelated initials must not match.");
    Assert(!FileNameSearch.Matches("Report", "rp"), "English words must not collapse to initials.");
    using var workspace = TemporaryWorkspace.Create();
    Directory.CreateDirectory(Path.Combine(workspace.Root, "人物素材"));
    File.WriteAllText(Path.Combine(workspace.Root, "人物设定.docx"), "fixture");
    var browser = new FileSystemBrowser();
    Assert(browser.GetItems(workspace.Root, workspace.Root, new FileQueryOptions(SearchText: "rw")).Count == 2, "Initials search must include both folders and files.");
    Assert(browser.GetItems(workspace.Root, workspace.Root, new FileQueryOptions(Category: FileItemCategory.Document, SearchText: "rw")).Count == 1, "Initials and category filtering must compose.");
    return Task.CompletedTask;
}

static async Task TestTextPreviewReader()
{
    using var workspace = TemporaryWorkspace.Create();
    const string content = "---\nconst title = '你好';\n---\n<h1>{title}</h1>";
    foreach (var name in new[] { "page.astro", ".gitignore", ".gitattributes", "HEAD", "LICENSE", "notes.unregistered" })
    {
        var path = Path.Combine(workspace.Root, name);
        await File.WriteAllTextAsync(path, content);
        var result = await TextPreviewReader.ReadAsync(workspace.Root, path);
        Assert(result.Text == content, $"{name} must be previewable without source execution.");
    }
    Assert(TextPreviewReader.IsKnownText(".astro") && TextPreviewReader.IsCode(".astro"), "Astro must use the catalog's code preview.");
    var unicode = Path.Combine(workspace.Root, "utf16.unknown");
    await File.WriteAllTextAsync(unicode, "中文文本\r\n第二行", Encoding.Unicode);
    Assert((await TextPreviewReader.ReadAsync(workspace.Root, unicode)).Text == "中文文本\r\n第二行", "BOM-marked UTF-16 must be decoded.");
    var binary = Path.Combine(workspace.Root, "binary.unknown");
    await File.WriteAllBytesAsync(binary, [0, 1, 2, 255]);
    Assert((await TextPreviewReader.ReadAsync(workspace.Root, binary)).Text is null, "Binary must fall back without decoding garbage.");
    await File.WriteAllBytesAsync(binary, [0xC3, 0x28]);
    Assert((await TextPreviewReader.ReadAsync(workspace.Root, binary)).Text is null, "Invalid UTF-8 must not silently become replacement characters.");
    await File.WriteAllBytesAsync(binary, new byte[TextPreviewReader.MaximumBytes + 1]);
    Assert((await TextPreviewReader.ReadAsync(workspace.Root, binary)).Text is null, "Oversized files must not render.");
    await File.WriteAllTextAsync(binary, "");
    Assert((await TextPreviewReader.ReadAsync(workspace.Root, binary)).Text == "", "Empty files are valid text.");
    var blocked = false;
    try { await TextPreviewReader.ReadAsync(workspace.Root, Path.Combine(workspace.Root, "..", "outside.txt")); }
    catch (UnauthorizedAccessException) { blocked = true; }
    Assert(blocked, "Text sniffing must not escape the project.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var canceled = false;
    try { await TextPreviewReader.ReadAsync(workspace.Root, binary, cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Assert(canceled, "Text reads must respect cancellation.");
}

static Task TestSearchAndParent()
{
    using var workspace = TemporaryWorkspace.Create();
    var child = Directory.CreateDirectory(Path.Combine(workspace.Root, "child"));
    File.WriteAllText(Path.Combine(workspace.Root, "Hero.ASTRO"), "source");
    File.WriteAllText(Path.Combine(workspace.Root, "hero.txt"), "text");
    File.WriteAllText(Path.Combine(child.FullName, "hero-nested.astro"), "nested");
    var browser = new FileSystemBrowser();
    var result = browser.GetItems(workspace.Root, workspace.Root, new FileQueryOptions(SearchText: " HERO "));
    Assert(result.Count == 2, "Case-insensitive trimmed name search must stay in the current folder.");
    result = browser.GetItems(workspace.Root, workspace.Root, new FileQueryOptions(Category: FileItemCategory.Code, SearchText: "hero"));
    Assert(result.Count == 1 && result[0].Name == "Hero.ASTRO", "Search must compose with category filtering.");
    Assert(browser.GetItems(workspace.Root, workspace.Root, new FileQueryOptions(SearchText: "missing")).Count == 0, "No matches must return an empty result.");
    Assert(browser.GetParentFolder(workspace.Root, child.FullName) == workspace.Root, "Up must navigate exactly one level.");
    Assert(browser.GetParentFolder(workspace.Root, workspace.Root) is null, "Up must stop at root.");
    AssertThrows<UnauthorizedAccessException>(() => browser.GetParentFolder(workspace.Root, Path.Combine(workspace.Root, "..")));
    return Task.CompletedTask;
}

static async Task TestProjectAlias()
{
    using var workspace = TemporaryWorkspace.Create();
    var root = Directory.CreateDirectory(Path.Combine(workspace.Root, "original"));
    var path = Path.Combine(workspace.Root, "registry.json");
    var store = new ProjectRegistryStore(path);
    var initial = await store.AddAsync(root.FullName);
    var project = initial.ActiveProject!;
    Assert(project.ToString() == "original", "Fallback display must be a friendly project name, never record internals.");
    Assert(project.Name == "original", "Adding a project must keep the default directory name.");
    await store.RenameAsync(project.Id, "  项目别名  ");
    var loaded = await new ProjectRegistryStore(path).LoadAsync();
    Assert(loaded.ActiveProject?.Id == project.Id && loaded.ActiveProject.Name == "项目别名", "Alias must survive reload without changing active identity.");
    Assert(loaded.ActiveProject!.RootPath == root.FullName && Directory.Exists(root.FullName), "Alias must not rename the directory.");
    Assert((await store.AddAsync(root.FullName)).ActiveProject!.Name == "项目别名", "Re-registering a path must preserve its alias.");
    File.WriteAllText(path, "broken");
    Assert((await new ProjectRegistryStore(path).LoadAsync()).ActiveProject!.Name == "项目别名", "Backup recovery must preserve the alias.");
    foreach (var invalid in new[] { " ", "bad\nname", new string('x', 81) })
    {
        var rejected = false;
        try { await store.RenameAsync(project.Id, invalid); }
        catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Invalid aliases must be rejected before mutation.");
    }
}

static Task TestBoundaryAcceptsRootAndDescendants()
{
    using var workspace = TemporaryWorkspace.Create();
    var child = Directory.CreateDirectory(Path.Combine(workspace.Root, "assets", "images"));
    var boundary = new PathBoundary(workspace.Root);

    Assert(boundary.Contains(workspace.Root), "The project root must be allowed.");
    Assert(boundary.Contains(child.FullName), "A child directory must be allowed.");
    Assert(boundary.IsSafeExistingPath(child.FullName), "A normal child directory must be safe.");

    var driveRoot = Path.GetPathRoot(workspace.Root)!;
    var driveBoundary = new PathBoundary(driveRoot);
    Assert(driveBoundary.Contains(workspace.Root), "A drive-root project must still contain its descendants.");
    return Task.CompletedTask;
}

static Task TestBoundaryRejectsEscapes()
{
    using var workspace = TemporaryWorkspace.Create();
    var sibling = Directory.CreateDirectory(workspace.Root + "-other");
    var boundary = new PathBoundary(workspace.Root);

    Assert(!boundary.Contains(sibling.FullName), "A prefix sibling must not pass the boundary check.");

    var traversal = Path.Combine(workspace.Root, "..", sibling.Name);
    Assert(!boundary.Contains(traversal), "A traversal path must not escape the root.");
    AssertThrows<UnauthorizedAccessException>(() => boundary.EnsureSafe(sibling.FullName));
    return Task.CompletedTask;
}

static Task TestNaturalSort()
{
    var names = new[] { "shot10.png", "shot2.png", "shot1.png" };
    Array.Sort(names, NaturalStringComparer.OrdinalIgnoreCase);
    Assert(names.SequenceEqual(["shot1.png", "shot2.png", "shot10.png"]), "Numeric segments must sort naturally.");
    return Task.CompletedTask;
}

static Task TestFileVisuals()
{
    static FileSystemItem Item(string name, string extension, FileItemCategory category, bool isDirectory = false) =>
        new(
            name,
            Path.Combine("C:\\project", name),
            isDirectory,
            isDirectory ? null : 1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            extension,
            category);

    var representatives = new (string Name, string Extension, FileItemCategory Category, bool IsDirectory, FileVisualKind Expected)[]
    {
        ("assets", string.Empty, FileItemCategory.Folder, true, FileVisualKind.Folder),
        ("frame.png", ".png", FileItemCategory.Image, false, FileVisualKind.Image),
        ("trailer.mp4", ".mp4", FileItemCategory.Video, false, FileVisualKind.Video),
        ("ambience.opus", ".opus", FileItemCategory.Audio, false, FileVisualKind.Audio),
        ("brief.pdf", ".pdf", FileItemCategory.Document, false, FileVisualKind.Pdf),
        ("script.docx", ".docx", FileItemCategory.Document, false, FileVisualKind.Word),
        ("shots.xlsx", ".xlsx", FileItemCategory.Document, false, FileVisualKind.Spreadsheet),
        ("pitch.pptx", ".pptx", FileItemCategory.Document, false, FileVisualKind.Presentation),
        ("README.md", ".md", FileItemCategory.Document, false, FileVisualKind.Markdown),
        ("notes.txt", ".txt", FileItemCategory.Document, false, FileVisualKind.Text),
        ("tool.py", ".py", FileItemCategory.Code, false, FileVisualKind.Code),
        ("project.json", ".json", FileItemCategory.Code, false, FileVisualKind.Data),
        ("catalog.sqlite3", ".sqlite3", FileItemCategory.Other, false, FileVisualKind.Database),
        ("delivery.zip", ".zip", FileItemCategory.Archive, false, FileVisualKind.Archive),
        ("build.ps1", ".ps1", FileItemCategory.Code, false, FileVisualKind.Script),
        ("setup.msi", ".msi", FileItemCategory.Other, false, FileVisualKind.Executable),
        ("display.woff2", ".woff2", FileItemCategory.Other, false, FileVisualKind.Font),
        ("index.html", ".html", FileItemCategory.Code, false, FileVisualKind.Web),
        ("message.eml", ".eml", FileItemCategory.Document, false, FileVisualKind.Mail),
        ("book.epub", ".epub", FileItemCategory.Document, false, FileVisualKind.Ebook),
        ("art.psd", ".psd", FileItemCategory.Other, false, FileVisualKind.RasterEditor),
        ("logo.ai", ".ai", FileItemCategory.Other, false, FileVisualKind.VectorEditor),
        ("wireframe.fig", ".fig", FileItemCategory.Other, false, FileVisualKind.UiPrototype),
        ("title.aep", ".aep", FileItemCategory.Other, false, FileVisualKind.MotionGraphics),
        ("shot.prproj", ".prproj", FileItemCategory.Video, false, FileVisualKind.VideoProject),
        ("scene.blend", ".blend", FileItemCategory.Other, false, FileVisualKind.Blender),
        ("mesh.fbx", ".fbx", FileItemCategory.Other, false, FileVisualKind.Mesh3D),
        ("plan.dwg", ".dwg", FileItemCategory.Other, false, FileVisualKind.Cad),
        ("negative.dng", ".dng", FileItemCategory.Image, false, FileVisualKind.CameraRaw),
        ("layout.indd", ".indd", FileItemCategory.Other, false, FileVisualKind.DesignPackage),
        ("legacy.pages", ".pages", FileItemCategory.Document, false, FileVisualKind.Document),
        ("unknown.zzz", ".zzz", FileItemCategory.Other, false, FileVisualKind.Other)
    };

    foreach (var representative in representatives)
    {
        var item = Item(representative.Name, representative.Extension, representative.Category, representative.IsDirectory);
        Assert(FileVisualClassifier.Classify(item) == representative.Expected,
            $"{representative.Extension} must map to {representative.Expected}.");
    }

    Assert(FileIconCatalog.IconFamilyCount == Enum.GetValues<FileVisualKind>().Length
           && FileIconCatalog.DistinctGlyphCount == FileIconCatalog.IconFamilyCount
           && Enum.GetValues<FileVisualKind>().All(kind => !string.IsNullOrEmpty(FileIconCatalog.Get(kind).Glyph)),
        "Every visual family must have one non-empty and distinct Fluent icon glyph.");

    var folder = Item("assets", string.Empty, FileItemCategory.Folder, isDirectory: true);
    var image = Item("frame.png", ".png", FileItemCategory.Image);
    var rawImage = Item("negative.dng", ".dng", FileItemCategory.Image);
    var pdf = Item("brief.pdf", ".pdf", FileItemCategory.Document);
    var word = Item("script.docx", ".docx", FileItemCategory.Document);
    var sheet = Item("shots.xlsx", ".xlsx", FileItemCategory.Document);
    var markdown = Item("README.md", ".md", FileItemCategory.Document);
    var json = Item("project.json", ".json", FileItemCategory.Code);
    var python = Item("tool.py", ".py", FileItemCategory.Code);
    var archive = Item("delivery.zip", ".zip", FileItemCategory.Archive);
    var video = Item("trailer.mp4", ".mp4", FileItemCategory.Video);
    var audio = Item("ambience.opus", ".opus", FileItemCategory.Audio);
    var script = Item("build.ps1", ".ps1", FileItemCategory.Code);
    var database = Item("catalog.sqlite3", ".sqlite3", FileItemCategory.Other);
    var creative = Item("shot.prproj", ".prproj", FileItemCategory.Video);
    var designPackage = Item("layout.indd", ".indd", FileItemCategory.Other);
    Assert(FileVisualClassifier.GetBadge(folder) == "DIR"
           && FileVisualClassifier.GetBadge(image) == string.Empty,
        "Folders must have a dedicated badge while image thumbnails remain badge-free.");
    Assert(FileVisualClassifier.GetTypeMonogram(image) == "PNG"
           && FileVisualClassifier.GetTypeMonogram(markdown) == "MD"
           && FileVisualClassifier.GetTypeMonogram(Item("LICENSE", string.Empty, FileItemCategory.Other)) == "FILE",
        "List monograms must remain visible before thumbnails load and for extensionless files.");
    Assert(FileVisualClassifier.GetBadge(pdf) == "PDF"
           && FileVisualClassifier.GetBadge(word) == "DOCX"
           && FileVisualClassifier.GetBadge(sheet) == "XLSX"
           && FileVisualClassifier.GetBadge(markdown) == "MD"
           && FileVisualClassifier.GetBadge(json) == "JSON"
           && FileVisualClassifier.GetBadge(python) == "PY"
           && FileVisualClassifier.GetBadge(archive) == "ZIP"
           && FileVisualClassifier.GetBadge(video) == "MP4"
           && FileVisualClassifier.GetBadge(audio) == "OPUS"
           && FileVisualClassifier.GetBadge(script) == "PS1"
           && FileVisualClassifier.GetBadge(database) == "DB"
           && FileVisualClassifier.GetBadge(rawImage) == "DNG"
           && FileVisualClassifier.GetBadge(creative) == "PR",
        "Badges must stay compact while distinguishing common file formats.");
    Assert(FileFormatCatalog.SupportedExtensionCount >= 220
           && FileCategoryClassifier.Classify(".svg", isDirectory: false) == FileItemCategory.Image
           && FileCategoryClassifier.Classify(".dng", isDirectory: false) == FileItemCategory.Image
           && FileCategoryClassifier.Classify(".webm", isDirectory: false) == FileItemCategory.Video
           && FileCategoryClassifier.Classify(".prproj", isDirectory: false) == FileItemCategory.Video
           && FileCategoryClassifier.Classify(".opus", isDirectory: false) == FileItemCategory.Audio
           && FileCategoryClassifier.Classify(".html", isDirectory: false) == FileItemCategory.Code
           && FileCategoryClassifier.Classify(".vue", isDirectory: false) == FileItemCategory.Code
           && FileCategoryClassifier.Classify(".zst", isDirectory: false) == FileItemCategory.Archive,
        "The default format catalog must cover common project and creative file specifications.");
    Assert(FileFormatCatalog.GetDisplayType(image) == "PNG 图片"
           && FileFormatCatalog.GetDisplayType(markdown) == "Markdown 文档"
           && FileFormatCatalog.GetDisplayType(video) == "MP4 视频"
           && FileFormatCatalog.GetDisplayType(database) == "数据库"
           && FileFormatCatalog.GetDisplayType(creative) == "Premiere 项目"
           && FileFormatCatalog.GetDisplayType(rawImage) == "DNG 相机原片"
           && FileFormatCatalog.GetDisplayType(designPackage) == "INDD 排版设计",
        "Known formats must expose readable type names for the list view.");
    return Task.CompletedTask;
}

static Task TestFileBrowser()
{
    using var workspace = TemporaryWorkspace.Create();
    Directory.CreateDirectory(Path.Combine(workspace.Root, "folder10"));
    Directory.CreateDirectory(Path.Combine(workspace.Root, "folder2"));
    File.WriteAllText(Path.Combine(workspace.Root, "image10.png"), "x");
    File.WriteAllText(Path.Combine(workspace.Root, "image2.png"), "x");
    File.WriteAllText(Path.Combine(workspace.Root, "notes.md"), "x");

    var browser = new FileSystemBrowser();
    var all = browser.GetItems(workspace.Root, workspace.Root, new FileQueryOptions());
    Assert(all[0].IsDirectory && all[1].IsDirectory, "Folders must be grouped before files.");
    Assert(all[0].Name == "folder2", "Folder names must use natural sorting.");

    var images = browser.GetItems(
        workspace.Root,
        workspace.Root,
        new FileQueryOptions(Category: FileItemCategory.Image));
    Assert(images.Count == 2 && images.All(item => item.IsImage), "The image filter must exclude non-images.");
    return Task.CompletedTask;
}

static Task TestFileBrowserScopeAndProgress()
{
    using var workspace = TemporaryWorkspace.Create();
    var selectedFolder = Directory.CreateDirectory(Path.Combine(workspace.Root, "selected"));
    var nestedFolder = Directory.CreateDirectory(Path.Combine(selectedFolder.FullName, "nested"));
    File.WriteAllText(Path.Combine(selectedFolder.FullName, "current.png"), "x");
    File.WriteAllText(Path.Combine(selectedFolder.FullName, "notes.txt"), "x");
    File.WriteAllText(Path.Combine(nestedFolder.FullName, "nested.png"), "x");

    var reportedCount = 0;
    var browser = new FileSystemBrowser();
    var images = browser.GetItems(
        workspace.Root,
        selectedFolder.FullName,
        new FileQueryOptions(Category: FileItemCategory.Image),
        new InlineProgress<int>(count => reportedCount = count),
        CancellationToken.None);

    Assert(images.Count == 1 && images[0].Name == "current.png",
        "Filtering a selected folder must not include matching files from child folders.");
    Assert(reportedCount == 3, "Progress must report every entry examined in the selected folder.");

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    AssertThrows<OperationCanceledException>(() => browser.GetItems(
        workspace.Root,
        selectedFolder.FullName,
        new FileQueryOptions(Category: FileItemCategory.Image),
        progress: null,
        cancellation.Token));
    return Task.CompletedTask;
}

static async Task TestProjectRegistry()
{
    using var workspace = TemporaryWorkspace.Create();
    var first = Directory.CreateDirectory(Path.Combine(workspace.Root, "first"));
    var second = Directory.CreateDirectory(Path.Combine(workspace.Root, "second"));
    var statePath = Path.Combine(workspace.Root, "state", "projects.json");
    var backupPath = Path.Combine(workspace.Root, "roaming-backup", "projects.backup.json");
    var store = new ProjectRegistryStore(statePath, backupPath);

    var firstState = await store.AddAsync(first.FullName);
    var secondState = await store.AddAsync(second.FullName);

    Assert(firstState.ActiveProject?.RootPath == first.FullName, "The first project must become active.");
    Assert(secondState.Projects.Count == 2, "Both projects must remain registered.");
    Assert(secondState.ActiveProject?.RootPath == second.FullName, "The most recently added project must become active.");
    Assert(firstState.Revision == 1 && secondState.Revision == 2, "Each registry mutation must advance the revision.");
    Assert(File.Exists(statePath) && File.Exists(backupPath), "The project registry must maintain primary and independent backup copies.");

    await File.WriteAllTextAsync(statePath, "{}");
    var recoveredFromEmptyPrimary = await store.LoadAsync();
    Assert(store.LastLoadRecoveredFromBackup, "An accidentally reset primary registry must recover from the newer backup.");
    Assert(recoveredFromEmptyPrimary.Projects.Count == 2, "Recovery must preserve every registered project.");

    await File.WriteAllTextAsync(statePath, "{ invalid json");
    var recoveredFromCorruptPrimary = await store.LoadAsync();
    Assert(store.LastLoadRecoveredFromBackup, "A corrupt primary registry must recover from the backup.");
    Assert(recoveredFromCorruptPrimary.Projects.Count == 2, "Corruption recovery must not replace the registry with an empty list.");

    File.Delete(statePath);
    var recoveredFromMissingPrimary = await store.LoadAsync();
    Assert(store.LastLoadRecoveredFromBackup, "A missing primary registry must recover from the backup.");
    Assert(recoveredFromMissingPrimary.Projects.Count == 2 && File.Exists(statePath), "Missing-primary recovery must repair the primary copy.");

    var restored = await store.SetActiveAsync(firstState.ActiveProject!.Id);
    Assert(restored.ActiveProject?.RootPath == first.FullName, "Exactly the requested project must become active.");

    await store.RemoveAsync(firstState.ActiveProject.Id);
    var afterRemoval = await store.LoadAsync();
    Assert(afterRemoval.Projects.Count == 1 && afterRemoval.Projects[0].RootPath == second.FullName, "An intentional removal must persist across all registry copies.");
    Assert(Directory.Exists(first.FullName), "Unregistering a project must never delete its root directory.");

    var unreadablePrimary = Path.Combine(workspace.Root, "unreadable", "projects.json");
    var unreadableBackup = Path.Combine(workspace.Root, "unreadable-backup", "projects.json");
    Directory.CreateDirectory(Path.GetDirectoryName(unreadablePrimary)!);
    Directory.CreateDirectory(Path.GetDirectoryName(unreadableBackup)!);
    await File.WriteAllTextAsync(unreadablePrimary, "{ broken");
    await File.WriteAllTextAsync(unreadableBackup, "{ broken");
    var unreadableStore = new ProjectRegistryStore(unreadablePrimary, unreadableBackup);
    var protectedFromOverwrite = false;
    try
    {
        await unreadableStore.LoadAsync();
    }
    catch (InvalidDataException)
    {
        protectedFromOverwrite = true;
    }

    Assert(protectedFromOverwrite, "When every copy is unreadable, loading must fail instead of silently returning an empty registry.");
}

static async Task TestAppSettingsStore()
{
    using var workspace = TemporaryWorkspace.Create();
    var statePath = Path.Combine(workspace.Root, "state", "settings.json");
    var store = new AppSettingsStore(statePath);
    var projectId = Guid.NewGuid();
    var state = new AppSettingsState
    {
        SpacePreviewEnabled = false,
        InspectorVisible = false,
        FilterRailVisible = true,
        RestoreWorkspace = true,
        CheckForUpdatesOnStartup = true,
        LastUpdateCheckUtc = new DateTimeOffset(2026, 8, 31, 8, 30, 0, TimeSpan.Zero),
        CloseToTrayEnabled = true,
        CloseToTrayConfigured = true,
        Theme = AppThemeNames.Graphite,
        Density = AppDensityNames.Compact,
        TreePaneWidth = 318,
        InspectorPaneWidth = 406,
        ProjectWorkspaces = new Dictionary<Guid, ProjectWorkspaceState>
        {
            [projectId] = new()
            {
                RelativeFolder = Path.Combine("assets", "images"),
                CategoryFilter = FileItemCategory.Image,
                SortField = FileSortField.ModifiedAt,
                SortDirection = SortDirection.Descending,
                GridView = false,
                IncludeSubfolders = true
            }
        }
    };

    await store.SaveAsync(state);
    var restored = await store.LoadAsync();
    var restoredWorkspace = restored.GetWorkspace(projectId);

    Assert(!restored.SpacePreviewEnabled
           && !restored.InspectorVisible
           && restored.CheckForUpdatesOnStartup
           && restored.CloseToTrayEnabled
           && restored.EffectiveCloseToTrayEnabled,
        "Application switches must survive a settings round trip.");
    Assert(restored.Theme == AppThemeNames.Graphite && restored.Density == AppDensityNames.Compact,
        "Theme and density must survive a settings round trip.");
    Assert(restored.TreePaneWidth == 318
           && restored.InspectorPaneWidth == 406
           && restored.LastUpdateCheckUtc == state.LastUpdateCheckUtc,
        "Update and resizable-pane preferences must survive a settings round trip.");
    Assert(restoredWorkspace?.CategoryFilter == FileItemCategory.Image
           && restoredWorkspace.SortDirection == SortDirection.Descending
           && !restoredWorkspace.GridView
           && restoredWorkspace.IncludeSubfolders,
        "Per-project folder, filter, sort, and view memory must survive a settings round trip.");

    await store.SaveAsync(state with { Theme = "Unknown", Density = "Unknown" });
    var normalized = await store.LoadAsync();
    Assert(normalized.Theme == AppThemeNames.Midnight
           && normalized.Density == AppDensityNames.Comfortable,
        "Unknown appearance values must fall back to supported defaults.");

    var legacyStatePath = Path.Combine(workspace.Root, "legacy", "settings.json");
    Directory.CreateDirectory(Path.GetDirectoryName(legacyStatePath)!);
    await File.WriteAllTextAsync(legacyStatePath, """
        {
          "CloseToTrayEnabled": false,
          "Theme": "Midnight",
          "Density": "Comfortable"
        }
        """);
    var legacyStore = new AppSettingsStore(legacyStatePath);
    var migrated = await legacyStore.LoadAsync();
    Assert(migrated.EffectiveCloseToTrayEnabled,
        "Settings created before the tray choice existed must default to notification-area behavior.");

    await legacyStore.SaveAsync(migrated with
    {
        CloseToTrayEnabled = false,
        CloseToTrayConfigured = true
    });
    var explicitlyDisabled = await legacyStore.LoadAsync();
    Assert(!explicitlyDisabled.EffectiveCloseToTrayEnabled,
        "An explicit user choice to fully exit must remain disabled.");
}

static Task TestRename()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var source = Path.Combine(workspace.Root, "shot01.png");
    File.WriteAllText(source, "image");

    var result = service.Rename(workspace.Root, source, "shot02.png");
    Assert(File.Exists(result.DestinationPath), "The renamed file must exist at its destination.");
    Assert(!File.Exists(source), "The original file name must no longer exist.");

    File.WriteAllText(Path.Combine(workspace.Root, "occupied.png"), "x");
    AssertThrows<IOException>(() => service.Rename(workspace.Root, result.DestinationPath, "occupied.png"));
    AssertThrows<ArgumentException>(() => service.Rename(workspace.Root, result.DestinationPath, "CON.txt"));
    AssertThrows<ArgumentException>(() => service.Rename(workspace.Root, result.DestinationPath, "bad?.png"));
    AssertThrows<InvalidOperationException>(() => service.Rename(workspace.Root, workspace.Root, "renamed-root"));
    return Task.CompletedTask;
}

static Task TestTransfer()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "destination"));
    var file = Path.Combine(workspace.Root, "notes.txt");
    File.WriteAllText(file, "notes");

    var moved = service.Transfer(workspace.Root, [file], destination.FullName, FileTransferMode.Move);
    Assert(moved.Count == 1, "A single move must return one result.");
    Assert(File.Exists(Path.Combine(destination.FullName, "notes.txt")), "The file must move into the destination folder.");

    var sourceDirectory = Directory.CreateDirectory(Path.Combine(workspace.Root, "assets"));
    File.WriteAllText(Path.Combine(sourceDirectory.FullName, "logo.svg"), "svg");
    var copied = service.Transfer(workspace.Root, [sourceDirectory.FullName], destination.FullName, FileTransferMode.Copy);
    Assert(copied.Count == 1, "A directory copy must return one result.");
    Assert(Directory.Exists(sourceDirectory.FullName), "Copying must preserve the source directory.");
    Assert(File.Exists(Path.Combine(destination.FullName, "assets", "logo.svg")), "Directory contents must be copied recursively.");
    return Task.CompletedTask;
}

static Task TestBatchCopy()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var sourceFolder = Directory.CreateDirectory(Path.Combine(workspace.Root, "source"));
    var destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "destination"));
    var firstFile = Path.Combine(sourceFolder.FullName, "first.txt");
    var secondFile = Path.Combine(sourceFolder.FullName, "second.txt");
    File.WriteAllText(firstFile, "first");
    File.WriteAllText(secondFile, "second");

    var copied = service.Transfer(
        workspace.Root,
        [firstFile, secondFile],
        destination.FullName,
        FileTransferMode.Copy);

    Assert(copied.Count == 2, "Every selected file must be included in a batch copy.");
    Assert(File.Exists(firstFile) && File.Exists(secondFile), "Batch copy must preserve every source file.");
    Assert(File.Exists(Path.Combine(destination.FullName, "first.txt"))
           && File.Exists(Path.Combine(destination.FullName, "second.txt")),
        "Every selected file must appear in the destination folder.");

    var pastedInPlace = service.ImportCopy(
        workspace.Root,
        [firstFile],
        sourceFolder.FullName,
        FileConflictResolution.KeepBoth);
    Assert(pastedInPlace.Count == 1
           && Path.GetFileName(pastedInPlace[0].DestinationPath) == "first (2).txt"
           && File.Exists(pastedInPlace[0].DestinationPath),
        "Pasting into the source folder with Keep Both must create a numbered duplicate.");
    return Task.CompletedTask;
}

static Task TestMarkdownPreviewParser()
{
    const string markdown = """
        # Project title

        Intro with **strong text** and `inline code`.

        ## Details

        - first item
        - [x] completed item
        1. ordered item

        > quoted note

        ```csharp
        var value = 42;
        ```
        """;

    var blocks = MarkdownPreviewParser.Parse(markdown);
    Assert(blocks.Count(block => block.Kind == MarkdownPreviewBlockKind.Heading) == 2,
        "Markdown headings must become distinct reading blocks.");
    Assert(blocks.Any(block => block.Kind == MarkdownPreviewBlockKind.Heading
                               && block.Level == 1
                               && block.Text == "Project title"),
        "The level-one title must retain its hierarchy and text.");
    Assert(blocks.Any(block => block.Kind == MarkdownPreviewBlockKind.BulletListItem
                               && block.IsChecked == true
                               && block.Text == "completed item"),
        "Task-list state must be preserved for the reading preview.");
    Assert(blocks.Any(block => block.Kind == MarkdownPreviewBlockKind.NumberedListItem
                               && block.Marker == "1."),
        "Ordered-list numbering must be preserved.");
    Assert(blocks.Any(block => block.Kind == MarkdownPreviewBlockKind.Code
                               && block.Language == "csharp"
                               && block.Text.Contains("value = 42", StringComparison.Ordinal)),
        "Fenced code must stay a code block with its language label.");
    Assert(blocks.All(block => !block.Text.Contains("http://", StringComparison.OrdinalIgnoreCase)
                               && !block.Text.Contains("https://", StringComparison.OrdinalIgnoreCase)),
        "The parser must not introduce or resolve external content.");
    return Task.CompletedTask;
}

static Task TestMarkdownTables()
{
    const string source = """
        # 分镜表

        |镜号|对白|参考|
        |:---|:---:|---:|
        |S01|中文 **加粗** 与 `x\|y`|[图片](assets/a.png)|
        |S02|甲\|乙 <script>alert(1)</script>|K02|
        |S03|缺一列|
        |S04|完整|K04|多余列|

        表格后正文。
        """;
    var blocks = MarkdownPreviewParser.Parse(source);
    var table = blocks.Single(block => block.Kind == MarkdownPreviewBlockKind.Table).Table!;
    Assert(table.Headers.SequenceEqual(new[] { "镜号", "对白", "参考" }), "Header columns must stay distinct.");
    Assert(table.Rows.Count == 4 && table.Rows[0][1].Contains("`x|y`") && table.Rows[1][1].StartsWith("甲|乙"), "Rows and escaped cell pipes must survive parsing.");
    Assert(table.Alignments.SequenceEqual(new[] { "left", "center", "right" }), "Delimiter alignment markers must be honored.");
    Assert(table.Rows[2][2] == string.Empty && table.Rows[3].Count == 3, "Uneven rows must normalize to the header width.");
    Assert(blocks.Last().Text == "表格后正文。", "A table must stop at the following paragraph.");
    var html = MarkdownHtmlRenderer.Render(source);
    Assert(html.Split("<tr>").Length - 1 == 5 && html.Contains("<th scope=\"col\"") && html.Contains("<td class=\"align-center\">"), "Rendered tables must have a semantic header and one row per record.");
    Assert(html.Contains("<strong>加粗</strong>") && html.Contains("data-pfh-href=\"assets/a.png\"") && html.Contains("&lt;script&gt;") && !html.Contains("<script>alert(1)</script>"), "Cell formatting and bounded links must work without raw HTML execution.");
    Assert(!MarkdownPreviewParser.Parse("甲|乙\n不是|分隔线").Any(block => block.Kind == MarkdownPreviewBlockKind.Table), "Pipes alone must not create a table.");
    Assert(MarkdownPreviewParser.Parse("```text\n|甲|乙|\n|---|---|\n|1|2|\n```").Single().Kind == MarkdownPreviewBlockKind.Code, "Fenced source must stay literal code.");
    var noOuterPipes = MarkdownPreviewParser.Parse("甲 | 乙\n--- | ---\n一 | 二").Single().Table!;
    Assert(noOuterPipes.Rows.Single()[1] == "二", "Tables without outer pipes must parse.");
    return Task.CompletedTask;
}

static Task TestMarkdownHtmlRenderer()
{
    const string markdown = """
        # 项目标题

        阅读 [项目文档](docs/readme.md)、[女主参考图](../人物图/女主%20正面.png) 和 [外部说明](https://example.com/help)。

        <script>alert('untrusted')</script>

        ```json
        { "enabled": true }
        ```

        ```text
        参考图：[代码块女主图](../人物图/代码块参考.png)
        ```
        """;

    var html = MarkdownHtmlRenderer.Render(markdown);
    Assert(html.Contains("Content-Security-Policy", StringComparison.Ordinal)
           && html.Contains("default-src 'none'", StringComparison.Ordinal),
        "The reading surface must block remote resources by default.");
    Assert(html.Contains("data-pfh-href=\"docs/readme.md\"", StringComparison.Ordinal)
           && html.Contains("data-pfh-href=\"../人物图/女主%20正面.png\"", StringComparison.Ordinal)
           && !html.Contains("<a href=\"https://example.com/help\"", StringComparison.Ordinal),
        "Document, relative-image, and external Markdown links must be delegated to the bounded host instead of navigating directly.");
    Assert(html.Contains("&lt;script&gt;alert", StringComparison.Ordinal)
           && !html.Contains("<script>alert('untrusted')</script>", StringComparison.Ordinal),
        "Raw Markdown HTML must remain encoded data.");
    Assert(html.Contains("复制整块", StringComparison.Ordinal)
           && html.Contains("copy-code", StringComparison.Ordinal)
           && html.Contains("chrome.webview.postMessage", StringComparison.Ordinal),
        "Fenced code must expose a whole-block copy request to the host.");
    Assert(html.Contains("参考图：[<a href=\"#\" data-pfh-href=\"../人物图/代码块参考.png\">代码块女主图</a>](../人物图/代码块参考.png)", StringComparison.Ordinal),
        "Markdown links inside a fenced prompt block must stay visually literal while exposing a bounded click target.");
    Assert(html.Contains("id=\"项目标题\"", StringComparison.Ordinal),
        "Unicode headings must retain local anchor navigation.");

    var warmGraphiteHtml = MarkdownHtmlRenderer.Render(markdown, MarkdownHtmlTheme.WarmGraphite);
    Assert(warmGraphiteHtml.Contains("<body class=\"warm-graphite\">", StringComparison.Ordinal)
           && warmGraphiteHtml.Contains("<main id=\"document\">", StringComparison.Ordinal)
           && warmGraphiteHtml.Contains("body.warm-graphite { background: #11110f; color: #b9b2a7; }", StringComparison.Ordinal)
           && warmGraphiteHtml.Contains("outline-color: #edb56d", StringComparison.Ordinal),
        "The warm-graphite reading surface must use the selected amber theme instead of inheriting midnight-blue accents.");

    var wrappedHtml = MarkdownHtmlRenderer.Render(markdown, MarkdownHtmlTheme.WarmGraphite, wrapCodeBlocks: true);
    Assert(wrappedHtml.Contains("<body class=\"warm-graphite wrap-code\">", StringComparison.Ordinal)
           && wrappedHtml.Contains("body.wrap-code pre { white-space: pre-wrap;", StringComparison.Ordinal),
        "The Markdown reading surface must expose a real code-block wrapping state.");
    return Task.CompletedTask;
}

static Task TestMarkdownProjectLinkResolver()
{
    using var workspace = TemporaryWorkspace.Create();
    var promptFolder = Directory.CreateDirectory(Path.Combine(workspace.Root, "提示词"));
    var imageFolder = Directory.CreateDirectory(Path.Combine(workspace.Root, "人物图"));
    var sourcePath = Path.Combine(promptFolder.FullName, "镜头03.md");
    var imagePath = Path.Combine(imageFolder.FullName, "女主 正面.png");
    File.WriteAllText(sourcePath, "# 镜头 03");
    File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);

    var resolvedImage = MarkdownProjectLinkResolver.ResolveLocalPath(
        workspace.Root,
        sourcePath,
        "../人物图/女主%20正面.png");
    Assert(string.Equals(resolvedImage, imagePath, StringComparison.OrdinalIgnoreCase),
        "A URL-encoded project-relative image link must resolve beside the Markdown source.");

    var resolvedFolder = MarkdownProjectLinkResolver.ResolveLocalPath(
        workspace.Root,
        sourcePath,
        "../人物图/");
    Assert(string.Equals(resolvedFolder, imageFolder.FullName, StringComparison.OrdinalIgnoreCase),
        "A project-relative folder link must resolve to the bounded folder.");

    AssertThrows<UnauthorizedAccessException>(() =>
        MarkdownProjectLinkResolver.ResolveLocalPath(
            workspace.Root,
            sourcePath,
            "../../项目外图片.png"));
    AssertThrows<UnauthorizedAccessException>(() =>
        MarkdownProjectLinkResolver.ResolveLocalPath(
            workspace.Root,
            sourcePath,
            imagePath));
    return Task.CompletedTask;
}

static async Task TestGitHubReleaseUpdateService()
{
    HttpRequestMessage? observedRequest = null;
    using var client = new HttpClient(new StubHttpMessageHandler(request =>
    {
        observedRequest = request;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "tag_name": "v0.0.4",
                  "name": "Project File Hub 0.0.4",
                  "body": "Safe update notification.",
                  "published_at": "2026-08-31T08:00:00Z",
                  "html_url": "https://github.com/anjero-sudo/Project-File-Hub/releases/tag/v0.0.4"
                }
                """, Encoding.UTF8, "application/json")
        };
    }));
    var service = new GitHubReleaseUpdateService(client);
    var available = await service.CheckAsync(new Version(0, 0, 3, 0));

    Assert(available.Status == ReleaseUpdateStatus.UpdateAvailable
           && available.LatestVersion == new Version(0, 0, 4),
        "A newer stable GitHub Release must be reported as available.");
    Assert(GitHubReleaseUpdateService.IsTrustedReleasePage(available.ReleasePageUri),
        "Only the expected repository release path may be returned to the desktop host.");
    Assert(observedRequest?.RequestUri?.Host == "api.github.com"
           && observedRequest.Headers.UserAgent.Count > 0,
        "The update check must call the official GitHub API with an identifiable client header.");

    using var noReleaseClient = new HttpClient(new StubHttpMessageHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.NotFound)));
    var noRelease = await new GitHubReleaseUpdateService(noReleaseClient)
        .CheckAsync(new Version(0, 0, 4));
    Assert(noRelease.Status == ReleaseUpdateStatus.NoPublishedRelease,
        "A repository without Releases must be a clear product state, not a network failure.");

    using var untrustedClient = new HttpClient(new StubHttpMessageHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "tag_name": "v0.0.5",
                  "html_url": "https://example.com/download/app.exe"
                }
                """, Encoding.UTF8, "application/json")
        }));
    var untrusted = await new GitHubReleaseUpdateService(untrustedClient)
        .CheckAsync(new Version(0, 0, 4));
    Assert(untrusted.ReleasePageUri == GitHubReleaseUpdateService.ReleasesPageUri,
        "An untrusted release URL must fall back to the canonical repository Releases page.");
    Assert(!GitHubReleaseUpdateService.TryParseReleaseVersion("latest", out _),
        "Non-version tags must not participate in update comparison.");
}

static Task TestCodePreviewTokenizer()
{
    const string source = "public class Demo { // comment\n    string label = \"hello\"; int count = 42;\n}";
    var tokens = CodePreviewTokenizer.Tokenize(source);

    Assert(string.Concat(tokens.Select(token => token.Text)) == source,
        "Syntax tokenization must never alter the previewed source text.");
    Assert(tokens.Any(token => token.Kind == CodePreviewTokenKind.Keyword && token.Text == "public"),
        "Language keywords must receive the Monokai keyword role.");
    Assert(tokens.Any(token => token.Kind == CodePreviewTokenKind.Comment && token.Text.Contains("comment", StringComparison.Ordinal)),
        "Line comments must receive the Monokai comment role.");
    Assert(tokens.Any(token => token.Kind == CodePreviewTokenKind.String && token.Text == "\"hello\""),
        "Quoted values must receive the Monokai string role.");
    Assert(tokens.Any(token => token.Kind == CodePreviewTokenKind.Number && token.Text == "42"),
        "Numeric values must receive the Monokai number role.");
    return Task.CompletedTask;
}

static Task TestPreviewZoomMath()
{
    var doubled = PreviewZoomMath.CalculateCenteredView(
        horizontalOffset: 0,
        verticalOffset: 0,
        viewportWidth: 1000,
        viewportHeight: 800,
        contentWidth: 1000,
        contentHeight: 800,
        currentZoomFactor: 1,
        requestedZoomFactor: 2,
        minimumZoomFactor: 0.5f,
        maximumZoomFactor: 8);
    Assert(doubled.ZoomFactor == 2
           && Math.Abs(doubled.HorizontalOffset - 500) < 0.001
           && Math.Abs(doubled.VerticalOffset - 400) < 0.001,
        "Doubling from a fitted image must keep its center at the center of the viewport.");

    var enlargedFromZoomedOut = PreviewZoomMath.CalculateCenteredView(
        horizontalOffset: 0,
        verticalOffset: 0,
        viewportWidth: 1000,
        viewportHeight: 800,
        contentWidth: 1000,
        contentHeight: 800,
        currentZoomFactor: 0.5f,
        requestedZoomFactor: 2,
        minimumZoomFactor: 0.5f,
        maximumZoomFactor: 8);
    Assert(Math.Abs(enlargedFromZoomedOut.HorizontalOffset - 500) < 0.001
           && Math.Abs(enlargedFromZoomedOut.VerticalOffset - 400) < 0.001,
        "Zooming from a centered image smaller than the viewport must preserve the content center.");

    Assert(Math.Abs(PreviewZoomMath.CalculatePanOffset(500, 120, 1000) - 380) < 0.001,
        "Dragging right must move the visible image offset left by the same distance.");
    Assert(PreviewZoomMath.CalculatePanOffset(40, 120, 1000) == 0,
        "Mouse panning must stop at the leading image edge.");
    Assert(PreviewZoomMath.CalculatePanOffset(950, -120, 1000) == 1000,
        "Mouse panning must stop at the trailing image edge.");
    return Task.CompletedTask;
}

static Task TestTransferGuards()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var parent = Directory.CreateDirectory(Path.Combine(workspace.Root, "parent"));
    var child = Directory.CreateDirectory(Path.Combine(parent.FullName, "child"));
    var nestedFile = Path.Combine(child.FullName, "nested.txt");
    File.WriteAllText(nestedFile, "nested");

    AssertThrows<InvalidOperationException>(() =>
        service.PlanTransfer(workspace.Root, [parent.FullName], child.FullName, FileTransferMode.Move));
    AssertThrows<InvalidOperationException>(() =>
        service.PlanTransfer(workspace.Root, [parent.FullName], parent.FullName, FileTransferMode.Copy));
    AssertThrows<InvalidOperationException>(() =>
        service.PlanTransfer(workspace.Root, [parent.FullName, nestedFile], workspace.Root, FileTransferMode.Copy));
    AssertThrows<InvalidOperationException>(() =>
        service.PlanTransfer(workspace.Root, [child.FullName], parent.FullName, FileTransferMode.Move));
    return Task.CompletedTask;
}

static Task TestConflictPolicies()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "destination"));
    var source = Path.Combine(workspace.Root, "notes.txt");
    File.WriteAllText(source, "new-content");
    File.WriteAllText(Path.Combine(destination.FullName, "notes.txt"), "old-content");

    AssertThrows<FileConflictException>(() =>
        service.PlanTransfer(workspace.Root, [source], destination.FullName, FileTransferMode.Copy));

    var kept = service.Transfer(
        workspace.Root,
        [source],
        destination.FullName,
        FileTransferMode.Copy,
        FileConflictResolution.KeepBoth);
    Assert(Path.GetFileName(kept[0].DestinationPath) == "notes (2).txt", "Keep Both must generate a numbered name.");

    var replaced = service.Transfer(
        workspace.Root,
        [source],
        destination.FullName,
        FileTransferMode.Copy,
        FileConflictResolution.Replace);
    Assert(replaced.Count == 1, "Replacing a file conflict must complete one operation.");
    Assert(File.ReadAllText(Path.Combine(destination.FullName, "notes.txt")) == "new-content", "Replace must update the destination file.");

    var skipped = service.Transfer(
        workspace.Root,
        [source],
        destination.FullName,
        FileTransferMode.Copy,
        FileConflictResolution.Skip);
    Assert(skipped.Count == 0, "Skip must omit conflicting items.");
    return Task.CompletedTask;
}

static Task TestExternalImport()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var external = Directory.CreateDirectory(workspace.Root + "-other");
    var externalFile = Path.Combine(external.FullName, "reference.txt");
    File.WriteAllText(externalFile, "reference");
    var externalFolder = Directory.CreateDirectory(Path.Combine(external.FullName, "assets"));
    File.WriteAllText(Path.Combine(externalFolder.FullName, "logo.svg"), "svg");
    var destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "imports"));

    var results = service.ImportCopy(
        workspace.Root,
        [externalFile, externalFolder.FullName],
        destination.FullName);
    Assert(results.Count == 2, "External import must return both copied items.");
    Assert(File.Exists(Path.Combine(destination.FullName, "reference.txt")), "The external file must be copied into the project.");
    Assert(File.Exists(Path.Combine(destination.FullName, "assets", "logo.svg")), "The external directory must be copied recursively.");
    Assert(File.Exists(externalFile), "External import must never move or delete its source.");
    return Task.CompletedTask;
}

static Task TestRecyclePlanning()
{
    using var workspace = TemporaryWorkspace.Create();
    var service = new FileOperationService();
    var folder = Directory.CreateDirectory(Path.Combine(workspace.Root, "folder"));
    var nested = Path.Combine(folder.FullName, "nested.txt");
    File.WriteAllText(nested, "nested");

    var planned = service.PlanRecycle(workspace.Root, [nested]);
    Assert(planned.Count == 1 && planned[0] == nested, "A normal project file must be eligible for recycling.");
    AssertThrows<InvalidOperationException>(() => service.PlanRecycle(workspace.Root, [workspace.Root]));
    AssertThrows<InvalidOperationException>(() => service.PlanRecycle(workspace.Root, [folder.FullName, nested]));
    return Task.CompletedTask;
}

static async Task TestBoundedCacheBudgets()
{
    var loads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var cache = new BoundedAsyncCache<string>(maximumBytes: 10, maximumEntries: 2, concurrency: 1, maximumPending: 4, timeout: TimeSpan.FromSeconds(1));

    Task<CachedResource<string>> Load(string key, long bytes, CancellationToken _)
    {
        loads[key] = loads.GetValueOrDefault(key) + 1;
        return Task.FromResult(new CachedResource<string>($"{key}-{loads[key]}", bytes));
    }

    await cache.GetAsync("A", token => Load("A", 4, token));
    await cache.GetAsync("B", token => Load("B", 4, token));
    await cache.GetAsync("A", token => Load("A", 4, token));
    await cache.GetAsync("C", token => Load("C", 4, token));
    await cache.GetAsync("B", token => Load("B", 4, token));

    Assert(loads["A"] == 1 && loads["B"] == 2 && loads["C"] == 1,
        "Reading A must refresh its LRU position so adding C evicts B.");
    Assert(cache.State.Entries <= 2 && cache.State.EstimatedBytes <= 10,
        "Cached resources must stay inside both configured budgets.");

    await cache.GetAsync("oversized", token => Load("oversized", 11, token));
    await cache.GetAsync("oversized", token => Load("oversized", 11, token));
    Assert(loads["oversized"] == 2,
        "A resource larger than the byte budget must be delivered without being retained.");

    var attempts = 0;
    await AssertThrowsAsync<IOException>(() => cache.GetAsync("retry", _ =>
    {
        attempts++;
        throw new IOException("first attempt fails");
    }));
    var retried = await cache.GetAsync("retry", _ =>
    {
        attempts++;
        return Task.FromResult(new CachedResource<string>("recovered", 1));
    });
    Assert(retried == "recovered" && attempts == 2,
        "A failed factory must leave no poisoned cache entry and remain retryable.");
}

static async Task TestBoundedCacheSingleFlight()
{
    var cache = new BoundedAsyncCache<string>(1024, 4, 1, 4, TimeSpan.FromSeconds(2));
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factoryCalls = 0;

    async Task<CachedResource<string>> Load(CancellationToken _)
    {
        Interlocked.Increment(ref factoryCalls);
        started.TrySetResult();
        await release.Task;
        return new CachedResource<string>("shared", 8);
    }

    using var firstWaiterCancellation = new CancellationTokenSource();
    var first = cache.GetAsync("same", Load, firstWaiterCancellation.Token);
    var second = cache.GetAsync("same", Load);
    await started.Task;
    firstWaiterCancellation.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(() => first);
    release.TrySetResult();

    Assert(await second == "shared" && factoryCalls == 1,
        "Canceling one caller must not cancel the shared native operation for another waiter.");
}

static async Task TestBoundedCacheInvalidation()
{
    var cache = new BoundedAsyncCache<string>(1024, 4, 1, 4, TimeSpan.FromSeconds(2));
    for (var round = 0; round < 2; round++)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stale = cache.GetAsync("image", async _ =>
        {
            started.TrySetResult();
            await release.Task;
            return new CachedResource<string>("stale", 8);
        });
        await started.Task;
        cache.CancelPending();
        release.TrySetResult();
        await AssertThrowsAsync<OperationCanceledException>(() => stale);
        await WaitForCacheIdleAsync(cache);
    }

    var freshLoads = 0;
    var fresh = await cache.GetAsync("image", _ =>
    {
        freshLoads++;
        return Task.FromResult(new CachedResource<string>("fresh", 8));
    });
    Assert(fresh == "fresh" && freshLoads == 1 && cache.State.Entries == 1,
        "An invalidated generation must never publish its stale result into the cache.");
}

static async Task TestBoundedCacheTimeoutAndQueue()
{
    var cache = new BoundedAsyncCache<string>(1024, 4, 1, 2, TimeSpan.FromMilliseconds(120));
    var nativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseNative = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    var timedOut = cache.GetAsync("blocked", async _ =>
    {
        nativeStarted.TrySetResult();
        await releaseNative.Task; // Deliberately ignores cancellation like a stuck native decoder.
        return new CachedResource<string>("late", 8);
    });
    await nativeStarted.Task;
    await AssertThrowsAsync<TimeoutException>(() => timedOut);

    var replacementStarted = 0;
    var replacement = cache.GetAsync("replacement", _ =>
    {
        Interlocked.Increment(ref replacementStarted);
        return Task.FromResult(new CachedResource<string>("replacement", 8));
    });
    await AssertThrowsAsync<InvalidOperationException>(() =>
        cache.GetAsync("overflow", _ => Task.FromResult(new CachedResource<string>("overflow", 8))));
    await Task.Delay(25);
    Assert(cache.State.Running == 1 && replacementStarted == 0,
        "A timed-out native operation must keep the only concurrency slot until it actually returns.");

    releaseNative.TrySetResult();
    Assert(await replacement == "replacement",
        "Queued work must proceed after the real native operation releases its slot.");
    await WaitForCacheIdleAsync(cache);
    Assert(cache.State.Pending <= 2,
        "Outstanding cache work must never exceed the configured queue bound.");
}

static async Task TestProjectIndexSearchAndSort()
{
    using var workspace = TemporaryWorkspace.Create();
    var assets = Directory.CreateDirectory(Path.Combine(workspace.Root, "assets"));
    File.WriteAllText(Path.Combine(assets.FullName, "shot10.png"), "image");
    File.WriteAllText(Path.Combine(assets.FullName, "shot2.png"), "image");
    File.WriteAllText(Path.Combine(assets.FullName, "人物.png"), "image");
    var databasePath = workspace.Root + ".sort.index.db";

    try
    {
        await using var index = new ProjectIndexService(workspace.Root, databasePath);
        await index.InitializeAsync();
        var options = new FileQueryOptions(
            FileSortField.Name,
            SortDirection.Descending,
            FileItemCategory.Image);
        var indexed = await index.QuerySubtreeAsync(FileItemCategory.Image, assets.FullName, options);
        var direct = new FileSystemBrowser().GetItems(workspace.Root, assets.FullName, options);
        Assert(indexed.Select(item => item.Name).SequenceEqual(direct.Select(item => item.Name)),
            "Index and direct-folder paths must share the same natural sort semantics.");

        var searched = await index.QueryAsync(
            FileItemCategory.Image,
            options with { SearchText = "rw" });
        Assert(searched.Count == 1 && searched[0].Name == "人物.png",
            "Index queries must apply the same Chinese-initial filename search as direct browsing.");
    }
    finally
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

static async Task TestProjectIndex()
{
    using var workspace = TemporaryWorkspace.Create();
    var assets = Directory.CreateDirectory(Path.Combine(workspace.Root, "assets", "nested"));
    File.WriteAllText(Path.Combine(assets.FullName, "first.png"), "image");
    var other = Directory.CreateDirectory(Path.Combine(workspace.Root, "other"));
    File.WriteAllText(Path.Combine(other.FullName, "outside.png"), "image");
    File.WriteAllText(Path.Combine(workspace.Root, "Program.cs"), "class Program {}");
    var databasePath = workspace.Root + ".index.db";

    try
    {
        await using var index = new ProjectIndexService(workspace.Root, databasePath);
        await index.InitializeAsync();

        var images = await index.QueryAsync(
            FileItemCategory.Image,
            new FileQueryOptions(Category: FileItemCategory.Image));
        var code = await index.QueryAsync(
            FileItemCategory.Code,
            new FileQueryOptions(Category: FileItemCategory.Code));
        Assert(images.Count == 2, "The initial scan must find images in nested folders.");
        Assert(code.Count == 1 && code[0].Name == "Program.cs", "The initial scan must classify code files.");

        var scopedImages = await index.QuerySubtreeAsync(
            FileItemCategory.Image,
            Path.Combine(workspace.Root, "assets"),
            new FileQueryOptions(Category: FileItemCategory.Image));
        Assert(scopedImages.Count == 1 && scopedImages[0].Name == "first.png",
            "Recursive filtering must stay inside the currently selected folder subtree.");
        var escaped = false;
        try
        {
            await index.QuerySubtreeAsync(
                FileItemCategory.Image,
                workspace.Root + "-outside",
                new FileQueryOptions(Category: FileItemCategory.Image));
        }
        catch (UnauthorizedAccessException)
        {
            escaped = true;
        }

        Assert(escaped, "Recursive filtering must reject a folder outside the active project root.");

        File.WriteAllText(Path.Combine(assets.FullName, "second.png"), "image");
        var incrementalUpdateObserved = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100);
            images = await index.QueryAsync(
                FileItemCategory.Image,
                new FileQueryOptions(Category: FileItemCategory.Image));
            if (images.Count == 3)
            {
                incrementalUpdateObserved = true;
                break;
            }
        }

        Assert(incrementalUpdateObserved, "The filesystem watcher must add a newly created nested image to the index.");

        index.Pause();
        Assert(index.IsPaused, "The project index must expose its paused state to the tray menu.");
        File.WriteAllText(Path.Combine(assets.FullName, "paused.png"), "image");
        await Task.Delay(700);
        images = await index.QueryAsync(
            FileItemCategory.Image,
            new FileQueryOptions(Category: FileItemCategory.Image));
        Assert(images.Count == 3, "A paused project index must not process filesystem watcher changes.");

        index.Resume();
        Assert(!index.IsPaused, "Resuming the project index must clear its paused state.");
        var resumeReconciliationObserved = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100);
            images = await index.QueryAsync(
                FileItemCategory.Image,
                new FileQueryOptions(Category: FileItemCategory.Image));
            if (images.Count == 4)
            {
                resumeReconciliationObserved = true;
                break;
            }
        }

        Assert(resumeReconciliationObserved,
            "Resuming the project index must reconcile changes that occurred while it was paused.");
    }
    finally
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task AssertThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task WaitForCacheIdleAsync<T>(BoundedAsyncCache<T> cache)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        if (cache.State is { Pending: 0, Running: 0 })
        {
            return;
        }
        await Task.Delay(5);
    }

    throw new InvalidOperationException("Cache work did not return to idle after cancellation/completion.");
}

file sealed class TemporaryWorkspace : IDisposable
{
    private TemporaryWorkspace(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TemporaryWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "ProjectFileHub.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TemporaryWorkspace(root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        var sibling = Root + "-other";
        if (Directory.Exists(sibling))
        {
            Directory.Delete(sibling, recursive: true);
        }
    }
}

file sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

file sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responseFactory(request));
}
