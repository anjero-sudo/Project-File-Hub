namespace ProjectFileHub.Core.Models;

public enum FileSortField
{
    Name,
    ModifiedAt,
    CreatedAt,
    Type,
    Size
}

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed record FileQueryOptions(
    FileSortField SortField = FileSortField.Name,
    SortDirection Direction = SortDirection.Ascending,
    FileItemCategory? Category = null,
    string? SearchText = null)
{
    public bool MatchesName(string name) => Services.FileNameSearch.Matches(name, SearchText);
}
