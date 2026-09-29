using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using ProjectFileHub.App.Services;
using ProjectFileHub.Core.Models;
using ProjectFileHub.Core.Services;

namespace ProjectFileHub.App.ViewModels;

public sealed class ExplorerItemViewModel : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private Task? _thumbnailLoadTask;
    private int _thumbnailVersion;
    private int _thumbnailLeaseCount;
    private bool _isRenaming;
    private bool _isSelected;
    private string _renameText;
    private string _renameError = string.Empty;

    public ExplorerItemViewModel(FileSystemItem item, string? projectRoot = null, bool showProjectLocation = false)
    {
        Item = item;
        VisualKind = FileVisualClassifier.Classify(item);
        IconBadgeText = FileVisualClassifier.GetBadge(item);
        ListIconText = FileVisualClassifier.GetTypeMonogram(item);
        IconBrush = ResolveIconBrush(VisualKind);
        _renameText = item.Name;
        ShowProjectLocation = showProjectLocation && !string.IsNullOrWhiteSpace(projectRoot);
        if (ShowProjectLocation)
        {
            var parent = Path.GetDirectoryName(item.FullPath) ?? item.FullPath;
            var relative = Path.GetRelativePath(projectRoot!, parent);
            LocationText = relative == "." ? "项目根目录" : relative;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileSystemItem Item { get; }

    public string Name => Item.Name;

    public string FullPath => Item.FullPath;

    public bool IsDirectory => Item.IsDirectory;

    public string DisplayType => FileFormatCatalog.GetDisplayType(Item);

    public string ModifiedText => Item.ModifiedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string SizeText => Item.Size is long size ? FormatBytes(size) : string.Empty;

    public bool ShowProjectLocation { get; }

    public string LocationText { get; } = string.Empty;

    public Visibility LocationVisibility => ShowProjectLocation ? Visibility.Visible : Visibility.Collapsed;

    public FileVisualKind VisualKind { get; }

    public string IconBadgeText { get; }

    public string ListIconText { get; }

    public Visibility IconBadgeVisibility =>
        string.IsNullOrEmpty(IconBadgeText) ? Visibility.Collapsed : Visibility.Visible;

    public string IconDescription =>
        IsDirectory
            ? "文件夹"
            : $"{FileIconCatalog.Get(VisualKind).AccessibleName}，{DisplayType}";

    public SolidColorBrush IconBrush { get; }

    public string IconGlyph => FileIconCatalog.Get(VisualKind).Glyph;

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ThumbnailVisibility));
            OnPropertyChanged(nameof(IconVisibility));
            OnPropertyChanged(nameof(ListFolderIconVisibility));
            OnPropertyChanged(nameof(ListTypeIconVisibility));
        }
    }

    public Visibility ThumbnailVisibility => Thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility IconVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListFolderIconVisibility =>
        Thumbnail is null && IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListTypeIconVisibility =>
        Thumbnail is null && !IsDirectory ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionVisibility));
        }
    }

    public Visibility SelectionVisibility => IsSelected ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRenaming => _isRenaming;

    public string RenameText
    {
        get => _renameText;
        set
        {
            if (string.Equals(_renameText, value, StringComparison.Ordinal))
            {
                return;
            }

            _renameText = value;
            RenameError = string.Empty;
            OnPropertyChanged();
        }
    }

    public string RenameError
    {
        get => _renameError;
        private set
        {
            if (string.Equals(_renameError, value, StringComparison.Ordinal))
            {
                return;
            }

            _renameError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RenameErrorVisibility));
        }
    }

    public Visibility NameVisibility => IsRenaming ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RenameVisibility => IsRenaming ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RenameErrorVisibility =>
        IsRenaming && !string.IsNullOrWhiteSpace(RenameError) ? Visibility.Visible : Visibility.Collapsed;

    public void BeginRename()
    {
        _renameText = Name;
        _renameError = string.Empty;
        _isRenaming = true;
        NotifyRenameState();
    }

    public void SetRenameError(string message)
    {
        RenameError = message;
    }

    public void CancelRename()
    {
        _renameText = Name;
        _renameError = string.Empty;
        _isRenaming = false;
        NotifyRenameState();
    }

    internal void AcquireThumbnailLease()
    {
        if (Item.IsImage)
        {
            _thumbnailLeaseCount++;
        }
    }

    internal void ReleaseThumbnailLease()
    {
        if (!Item.IsImage || _thumbnailLeaseCount == 0)
        {
            return;
        }

        _thumbnailLeaseCount--;
        if (_thumbnailLeaseCount == 0)
        {
            ReleaseThumbnail();
        }
    }

    internal void ReleaseThumbnail()
    {
        _thumbnailLeaseCount = 0;
        _thumbnailVersion++;
        // A recycled container may become visible again while the cache's native
        // single-flight is still finishing. Let the new VM generation attach its
        // own waiter; the shared cache still prevents duplicate decode work.
        _thumbnailLoadTask = null;
        Thumbnail = null;
    }

    internal Task LoadThumbnailAsync(
        ImageLoadingService imageLoading,
        string projectRoot,
        CancellationToken cancellationToken)
    {
        if (!Item.IsImage || Thumbnail is not null || _thumbnailLeaseCount == 0)
        {
            return Task.CompletedTask;
        }

        if (_thumbnailLoadTask is { IsCompleted: false } running)
        {
            return running;
        }

        var version = _thumbnailVersion;
        var operation = LoadThumbnailCoreAsync(imageLoading, projectRoot, version, cancellationToken);
        _thumbnailLoadTask = operation;
        return ClearCompletedThumbnailTaskAsync(operation);
    }

    private async Task LoadThumbnailCoreAsync(
        ImageLoadingService imageLoading,
        string projectRoot,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            var bitmap = await imageLoading.LoadThumbnailAsync(projectRoot, Item.FullPath, cancellationToken);
            if (!cancellationToken.IsCancellationRequested
                && version == _thumbnailVersion
                && _thumbnailLeaseCount > 0)
            {
                Thumbnail = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // Container recycling, navigation, and stale generations are expected.
        }
        catch (Exception)
        {
            // The service records path, HRESULT, queue state, and memory evidence.
            // Keep the category glyph when Windows cannot decode a thumbnail.
        }
    }

    private async Task ClearCompletedThumbnailTaskAsync(Task operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_thumbnailLoadTask, operation))
            {
                _thumbnailLoadTask = null;
            }
        }
    }

    public static string FormatBytes(long size)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)size;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    private static SolidColorBrush ResolveIconBrush(FileVisualKind kind)
    {
        var resourceKey = kind switch
        {
            FileVisualKind.Folder => "HubFileFolderBrush",
            FileVisualKind.Image => "HubFileImageBrush",
            FileVisualKind.Video => "HubFileVideoBrush",
            FileVisualKind.Audio => "HubFileAudioBrush",
            FileVisualKind.Pdf => "HubFilePdfBrush",
            FileVisualKind.Word => "HubFileWordBrush",
            FileVisualKind.Spreadsheet => "HubFileSpreadsheetBrush",
            FileVisualKind.Presentation => "HubFilePresentationBrush",
            FileVisualKind.Markdown => "HubFileMarkdownBrush",
            FileVisualKind.Text => "HubFileTextBrush",
            FileVisualKind.Code => "HubFileCodeBrush",
            FileVisualKind.Data => "HubFileDataBrush",
            FileVisualKind.Database => "HubFileDatabaseBrush",
            FileVisualKind.Archive => "HubFileArchiveBrush",
            FileVisualKind.Script => "HubFileCodeBrush",
            FileVisualKind.Executable => "HubFileExecutableBrush",
            FileVisualKind.Font => "HubFileFontBrush",
            FileVisualKind.Web => "HubFileMarkdownBrush",
            FileVisualKind.Mail => "HubFileWordBrush",
            FileVisualKind.Ebook => "HubFileDocumentBrush",
            FileVisualKind.RasterEditor => "HubFileImageBrush",
            FileVisualKind.VectorEditor => "HubFilePresentationBrush",
            FileVisualKind.UiPrototype => "HubFileDatabaseBrush",
            FileVisualKind.MotionGraphics or FileVisualKind.VideoProject => "HubFileVideoBrush",
            FileVisualKind.Blender => "HubFilePresentationBrush",
            FileVisualKind.Mesh3D => "HubFileDataBrush",
            FileVisualKind.Cad => "HubFileCodeBrush",
            FileVisualKind.CameraRaw => "HubFileImageBrush",
            FileVisualKind.DesignPackage => "HubFileFontBrush",
            FileVisualKind.Document => "HubFileDocumentBrush",
            _ => "HubFileOtherBrush"
        };

        return (SolidColorBrush)Application.Current.Resources[resourceKey];
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void NotifyRenameState()
    {
        OnPropertyChanged(nameof(IsRenaming));
        OnPropertyChanged(nameof(RenameText));
        OnPropertyChanged(nameof(RenameError));
        OnPropertyChanged(nameof(NameVisibility));
        OnPropertyChanged(nameof(RenameVisibility));
        OnPropertyChanged(nameof(RenameErrorVisibility));
    }
}
