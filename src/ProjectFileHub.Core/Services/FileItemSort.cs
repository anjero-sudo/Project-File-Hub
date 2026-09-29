using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

internal static class FileItemSort
{
    public static IReadOnlyList<FileSystemItem> Apply(IEnumerable<FileSystemItem> source, FileQueryOptions options, bool directoriesFirst = false)
    {
        var comparer = NaturalStringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<FileSystemItem> ordered = options.SortField switch
        {
            FileSortField.ModifiedAt => source.OrderBy(item => item.ModifiedAt),
            FileSortField.CreatedAt => source.OrderBy(item => item.CreatedAt),
            FileSortField.Type => source.OrderBy(item => item.DisplayType, comparer),
            FileSortField.Size => source.OrderBy(item => item.Size ?? -1),
            _ => source.OrderBy(item => item.Name, comparer)
        };
        ordered = ordered.ThenBy(item => item.Name, comparer);
        IEnumerable<FileSystemItem> sorted = options.Direction == SortDirection.Descending ? ordered.Reverse() : ordered;
        return (directoriesFirst ? sorted.OrderByDescending(item => item.IsDirectory) : sorted).ToArray();
    }
}
