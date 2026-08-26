namespace ProjectFileHub.Core.Models;

public sealed record RegisteredProject(
    Guid Id,
    string Name,
    string RootPath,
    DateTimeOffset AddedAt)
{
    public static RegisteredProject Create(string rootPath)
    {
        var normalizedRoot = PathBoundary.NormalizeRoot(rootPath);
        var name = new DirectoryInfo(normalizedRoot).Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = normalizedRoot;
        }

        return new RegisteredProject(Guid.NewGuid(), name, normalizedRoot, DateTimeOffset.UtcNow);
    }
}
