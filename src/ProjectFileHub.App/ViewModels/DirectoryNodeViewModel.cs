namespace ProjectFileHub.App.ViewModels;

public sealed record DirectoryNodeViewModel(string Name, string FullPath)
{
    public override string ToString() => Name;
}
