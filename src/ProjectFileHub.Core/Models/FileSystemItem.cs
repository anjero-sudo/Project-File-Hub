namespace ProjectFileHub.Core.Models;

public sealed record FileSystemItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? Size,
    DateTimeOffset ModifiedAt,
    DateTimeOffset CreatedAt,
    string Extension,
    FileItemCategory Category)
{
    public bool IsImage => Category == FileItemCategory.Image;

    public string DisplayType => IsDirectory
        ? "文件夹"
        : string.IsNullOrWhiteSpace(Extension)
            ? "文件"
            : Extension.TrimStart('.').ToUpperInvariant();
}
