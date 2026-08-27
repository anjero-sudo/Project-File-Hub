using ProjectFileHub.Core;
using ProjectFileHub.Core.Models;
using ProjectFileHub.Core.Services;

var tests = new (string Name, Func<Task> Run)[]
{
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
    ("Code preview tokenization preserves text and identifies Monokai token roles", TestCodePreviewTokenizer),
    ("Image preview zoom keeps the viewport center and clamps mouse panning", TestPreviewZoomMath),
    ("Transfer blocks self, subtree and nested selections", TestTransferGuards),
    ("Conflict policies keep both, replace files and skip", TestConflictPolicies),
    ("External import copies files and folders into the project", TestExternalImport),
    ("Recycle planning protects the project root and nested selections", TestRecyclePlanning),
    ("SQLite project index scans subfolders and tracks new files", TestProjectIndex)
};

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
    Assert(FileVisualClassifier.GetBadge(folder) == string.Empty
           && FileVisualClassifier.GetBadge(image) == string.Empty,
        "Folders and image thumbnails must not receive extension badges.");
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
        CloseToTrayEnabled = true,
        CloseToTrayConfigured = true,
        Theme = AppThemeNames.Graphite,
        Density = AppDensityNames.Compact,
        ProjectWorkspaces = new Dictionary<Guid, ProjectWorkspaceState>
        {
            [projectId] = new()
            {
                RelativeFolder = Path.Combine("assets", "images"),
                CategoryFilter = FileItemCategory.Image,
                SortField = FileSortField.ModifiedAt,
                SortDirection = SortDirection.Descending,
                GridView = false
            }
        }
    };

    await store.SaveAsync(state);
    var restored = await store.LoadAsync();
    var restoredWorkspace = restored.GetWorkspace(projectId);

    Assert(!restored.SpacePreviewEnabled
           && !restored.InspectorVisible
           && restored.CloseToTrayEnabled
           && restored.EffectiveCloseToTrayEnabled,
        "Application switches must survive a settings round trip.");
    Assert(restored.Theme == AppThemeNames.Graphite && restored.Density == AppDensityNames.Compact,
        "Theme and density must survive a settings round trip.");
    Assert(restoredWorkspace?.CategoryFilter == FileItemCategory.Image
           && restoredWorkspace.SortDirection == SortDirection.Descending
           && !restoredWorkspace.GridView,
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

static async Task TestProjectIndex()
{
    using var workspace = TemporaryWorkspace.Create();
    var assets = Directory.CreateDirectory(Path.Combine(workspace.Root, "assets", "nested"));
    File.WriteAllText(Path.Combine(assets.FullName, "first.png"), "image");
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
        Assert(images.Count == 1 && images[0].Name == "first.png", "The initial scan must find images in nested folders.");
        Assert(code.Count == 1 && code[0].Name == "Program.cs", "The initial scan must classify code files.");

        File.WriteAllText(Path.Combine(assets.FullName, "second.png"), "image");
        var incrementalUpdateObserved = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100);
            images = await index.QueryAsync(
                FileItemCategory.Image,
                new FileQueryOptions(Category: FileItemCategory.Image));
            if (images.Count == 2)
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
        Assert(images.Count == 2, "A paused project index must not process filesystem watcher changes.");

        index.Resume();
        Assert(!index.IsPaused, "Resuming the project index must clear its paused state.");
        var resumeReconciliationObserved = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100);
            images = await index.QueryAsync(
                FileItemCategory.Image,
                new FileQueryOptions(Category: FileItemCategory.Image));
            if (images.Count == 3)
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
