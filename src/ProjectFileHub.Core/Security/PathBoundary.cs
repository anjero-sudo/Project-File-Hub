namespace ProjectFileHub.Core;

public sealed class PathBoundary
{
    private readonly string _rootWithSeparator;

    public PathBoundary(string rootPath)
    {
        RootPath = NormalizeRoot(rootPath);
        _rootWithSeparator = RootPath.EndsWith(Path.DirectorySeparatorChar)
            ? RootPath
            : RootPath + Path.DirectorySeparatorChar;
    }

    public string RootPath { get; }

    public static string NormalizeRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullPath = Path.GetFullPath(rootPath);
        var root = Path.GetPathRoot(fullPath);

        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath.TrimEnd(Path.AltDirectorySeparatorChar);
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public bool Contains(string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        var candidate = Path.GetFullPath(candidatePath);
        return string.Equals(candidate, RootPath, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(_rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsSafeExistingPath(string candidatePath)
    {
        if (!Contains(candidatePath))
        {
            return false;
        }

        var candidate = Path.GetFullPath(candidatePath);
        var current = Directory.Exists(candidate)
            ? new DirectoryInfo(candidate)
            : File.Exists(candidate)
                ? new FileInfo(candidate).Directory
                : new DirectoryInfo(Path.GetDirectoryName(candidate) ?? candidate);

        while (current is not null && Contains(current.FullName))
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            if (string.Equals(current.FullName, RootPath, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent;
        }

        if (File.Exists(candidate))
        {
            var attributes = File.GetAttributes(candidate);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }
        }

        return true;
    }

    public string EnsureSafe(string candidatePath, bool mustExist = true)
    {
        var candidate = Path.GetFullPath(candidatePath);

        if (!Contains(candidate))
        {
            throw new UnauthorizedAccessException("目标路径超出了当前项目根目录。");
        }

        if (mustExist && !File.Exists(candidate) && !Directory.Exists(candidate))
        {
            throw new FileNotFoundException("目标路径不存在。", candidate);
        }

        if (!IsSafeExistingPath(candidate))
        {
            throw new UnauthorizedAccessException("目标路径包含不受信任的符号链接或目录联接。");
        }

        return candidate;
    }

    public string GetRelativePath(string candidatePath) =>
        Path.GetRelativePath(RootPath, EnsureSafe(candidatePath));
}
