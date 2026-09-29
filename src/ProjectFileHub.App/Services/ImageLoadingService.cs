using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using ProjectFileHub.App.Diagnostics;
using ProjectFileHub.Core;
using ProjectFileHub.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace ProjectFileHub.App.Services;

// Owned and called by the window's UI thread. Cached BitmapImages never cross apartments.
internal sealed class ImageLoadingService
{
    private const int ThumbnailLongestEdge = 320;
    private const int PreviewLongestEdge = 4096;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly BoundedAsyncCache<BitmapImage> _thumbnails = new(32 * 1024 * 1024, 128, 2, 128, TimeSpan.FromSeconds(10));
    private readonly BoundedAsyncCache<BitmapImage> _previews = new(128 * 1024 * 1024, 3, 1, 8, TimeSpan.FromSeconds(15));

    public ImageLoadingService(DispatcherQueue dispatcherQueue) =>
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));

    public Task<BitmapImage> LoadThumbnailAsync(string root, string path, CancellationToken cancellationToken) =>
        LoadAsync(root, path, thumbnail: true, cancellationToken);

    public Task<BitmapImage> LoadPreviewAsync(string root, string path, CancellationToken cancellationToken) =>
        LoadAsync(root, path, thumbnail: false, cancellationToken);

    public void CancelThumbnails(string reason)
    {
        _thumbnails.CancelPending();
        LogState($"cancel thumbnails · {reason}");
    }

    public void CancelPreviews(string reason)
    {
        _previews.CancelPending();
        LogState($"cancel previews · {reason}");
    }

    public void ReleaseAll(string reason)
    {
        _thumbnails.CancelPending(clearCache: true);
        _previews.CancelPending(clearCache: true);
        LogState(reason);
    }

    public void LogState(string reason) => AppDiagnostics.Log(
        $"Image resources · {reason} · thumbnails={_thumbnails.State} · previews={_previews.State} · managedBytes={GC.GetTotalMemory(false)} · workingSet={Environment.WorkingSet}");

    private async Task<BitmapImage> LoadAsync(string root, string path, bool thumbnail, CancellationToken cancellationToken)
    {
        EnsureUiThread();
        cancellationToken.ThrowIfCancellationRequested();
        var boundary = new PathBoundary(root);
        var safePath = boundary.EnsureSafe(path);
        var info = new FileInfo(safePath);
        // Changes to an image invalidate its cache entry, even before the index catches up.
        var key = $"{safePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var cache = thumbnail ? _thumbnails : _previews;
        try
        {
            return await cache.GetAsync(key, async token =>
            {
                token.ThrowIfCancellationRequested();
                boundary.EnsureSafe(safePath);
                var file = await StorageFile.GetFileFromPathAsync(safePath);
                token.ThrowIfCancellationRequested();
                if (thumbnail)
                {
                    using var stream = await file.GetThumbnailAsync(
                        ThumbnailMode.PicturesView,
                        ThumbnailLongestEdge,
                        ThumbnailOptions.ResizeThumbnail);
                    token.ThrowIfCancellationRequested();
                    if (stream is null || stream.Size == 0) throw new IOException("Windows 未提供此图片的缩略图。");
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    token.ThrowIfCancellationRequested();
                    var (width, height) = GetDecodeSize(
                        decoder.PixelWidth,
                        decoder.PixelHeight,
                        ThumbnailLongestEdge);
                    stream.Seek(0);
                    var bitmap = new BitmapImage
                    {
                        DecodePixelWidth = width,
                        DecodePixelHeight = height,
                        DecodePixelType = DecodePixelType.Physical
                    };
                    await bitmap.SetSourceAsync(stream);
                    token.ThrowIfCancellationRequested();
                    return new CachedResource<BitmapImage>(bitmap, (long)width * height * 4);
                }
                else
                {
                    using var stream = await file.OpenReadAsync();
                    token.ThrowIfCancellationRequested();
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    token.ThrowIfCancellationRequested();
                    // Preserve native 2K/4K detail; cap unusually large sources before allocation.
                    var (width, height) = GetDecodeSize(
                        decoder.PixelWidth,
                        decoder.PixelHeight,
                        PreviewLongestEdge);
                    stream.Seek(0);
                    var bitmap = new BitmapImage { DecodePixelWidth = width, DecodePixelHeight = height, DecodePixelType = DecodePixelType.Physical };
                    await bitmap.SetSourceAsync(stream);
                    token.ThrowIfCancellationRequested();
                    return new CachedResource<BitmapImage>(bitmap, (long)width * height * 4);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            AppDiagnostics.Log($"Image load failed · thumbnail={thumbnail} · {safePath}", exception);
            LogState("load failed");
            throw;
        }
    }

    private void EnsureUiThread()
    {
        if (!_dispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("图片资源只能在窗口 UI 线程加载。");
        }
    }

    private static (int Width, int Height) GetDecodeSize(uint pixelWidth, uint pixelHeight, int longestEdge)
    {
        if (pixelWidth == 0 || pixelHeight == 0)
        {
            throw new IOException("图片没有有效的像素尺寸。");
        }

        var scale = Math.Min(1.0, (double)longestEdge / Math.Max(pixelWidth, pixelHeight));
        return (
            Math.Max(1, (int)Math.Round(pixelWidth * scale)),
            Math.Max(1, (int)Math.Round(pixelHeight * scale)));
    }
}
