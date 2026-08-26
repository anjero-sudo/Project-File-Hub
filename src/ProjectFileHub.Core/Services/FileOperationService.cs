namespace ProjectFileHub.Core.Services;

public enum FileTransferMode
{
    Move,
    Copy
}

public enum FileConflictResolution
{
    Fail,
    Skip,
    KeepBoth,
    Replace
}

public sealed record FileOperationConflict(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory);

public sealed class FileConflictException : IOException
{
    public FileConflictException(IReadOnlyList<FileOperationConflict> conflicts)
        : base($"目标位置存在 {conflicts.Count} 个同名项目。")
    {
        Conflicts = conflicts;
    }

    public IReadOnlyList<FileOperationConflict> Conflicts { get; }
}

public sealed record FileOperationPlan(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory,
    FileTransferMode Mode,
    bool ReplaceExisting = false);

public sealed record FileOperationResult(
    string SourcePath,
    string DestinationPath,
    bool IsDirectory,
    FileTransferMode Mode,
    bool ReplacedExisting = false);

/// <summary>
/// Performs project-scoped file mutations. Every source, destination and
/// traversed directory is checked against the active project boundary.
/// </summary>
public sealed class FileOperationService
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public FileOperationResult Rename(string projectRoot, string sourcePath, string newName)
    {
        var boundary = new PathBoundary(projectRoot);
        var source = boundary.EnsureSafe(sourcePath);

        if (PathsEqual(source, boundary.RootPath))
        {
            throw new InvalidOperationException("不能重命名当前项目根目录。");
        }

        ValidateFileName(newName);

        var parent = Path.GetDirectoryName(source)
            ?? throw new InvalidOperationException("无法确定项目项的父目录。");
        boundary.EnsureSafe(parent);

        var destination = boundary.EnsureSafe(Path.Combine(parent, newName), mustExist: false);
        var isDirectory = Directory.Exists(source);

        if (PathsEqual(source, destination))
        {
            if (string.Equals(source, destination, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("新名称与当前名称相同。");
            }

            RenameCaseOnly(source, destination, parent, isDirectory);
        }
        else
        {
            EnsureDestinationAvailable(destination);
            MovePath(source, destination, isDirectory);
        }

        return new FileOperationResult(source, destination, isDirectory, FileTransferMode.Move);
    }

    public string CreateDirectory(string projectRoot, string parentDirectory, string name)
    {
        ValidateFileName(name);

        var boundary = new PathBoundary(projectRoot);
        var parent = boundary.EnsureSafe(parentDirectory);

        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("新文件夹的父目录不存在。");
        }

        var destination = boundary.EnsureSafe(Path.Combine(parent, name), mustExist: false);
        EnsureDestinationAvailable(destination);
        Directory.CreateDirectory(destination);
        return destination;
    }

    public IReadOnlyList<FileOperationPlan> PlanTransfer(
        string projectRoot,
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        FileTransferMode mode,
        FileConflictResolution conflictResolution = FileConflictResolution.Fail)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var boundary = new PathBoundary(projectRoot);
        var destinationFolder = boundary.EnsureSafe(destinationDirectory);

        if (!Directory.Exists(destinationFolder))
        {
            throw new DirectoryNotFoundException("拖放目标不是可用的文件夹。");
        }

        var sources = sourcePaths
            .Select(path => boundary.EnsureSafe(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            throw new InvalidOperationException("没有可移动或复制的项目。");
        }

        foreach (var source in sources)
        {
            if (PathsEqual(source, boundary.RootPath))
            {
                throw new InvalidOperationException("不能移动或复制当前项目根目录。");
            }
        }

        RejectNestedSourceSelection(sources);

        var plans = new List<FileOperationPlan>(sources.Length);
        var conflicts = new List<FileOperationConflict>();
        var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var name = Path.GetFileName(source);
            ValidateFileName(name);

            var destination = boundary.EnsureSafe(Path.Combine(destinationFolder, name), mustExist: false);
            var isDirectory = Directory.Exists(source);

            if (PathsEqual(source, destination))
            {
                throw new InvalidOperationException("项目已在目标文件夹中。");
            }

            if (isDirectory && (PathsEqual(destinationFolder, source) || IsDescendant(destinationFolder, source)))
            {
                throw new InvalidOperationException("不能把文件夹移动或复制到它自身或它的子文件夹中。");
            }

            var destinationExists = PathExists(destination) || reservedDestinations.Contains(destination);
            if (destinationExists)
            {
                var conflict = new FileOperationConflict(source, destination, isDirectory);
                if (conflictResolution == FileConflictResolution.Fail)
                {
                    conflicts.Add(conflict);
                    continue;
                }

                if (conflictResolution == FileConflictResolution.Skip)
                {
                    continue;
                }

                if (conflictResolution == FileConflictResolution.KeepBoth)
                {
                    destination = GetUniqueDestination(boundary, destinationFolder, name, isDirectory, reservedDestinations);
                }
                else if (isDirectory)
                {
                    throw new InvalidOperationException("文件夹冲突暂不支持直接替换，请选择“保留两者”或“跳过”。");
                }
            }

            reservedDestinations.Add(destination);
            plans.Add(new FileOperationPlan(
                source,
                destination,
                isDirectory,
                mode,
                ReplaceExisting: destinationExists && conflictResolution == FileConflictResolution.Replace));
        }

        if (conflicts.Count > 0)
        {
            throw new FileConflictException(conflicts);
        }

        return plans;
    }

    public IReadOnlyList<FileOperationResult> Transfer(
        string projectRoot,
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        FileTransferMode mode,
        FileConflictResolution conflictResolution = FileConflictResolution.Fail)
    {
        var boundary = new PathBoundary(projectRoot);
        var plans = PlanTransfer(projectRoot, sourcePaths, destinationDirectory, mode, conflictResolution);
        var results = new List<FileOperationResult>(plans.Count);

        foreach (var plan in plans)
        {
            if (plan.Mode == FileTransferMode.Move)
            {
                MovePath(plan.SourcePath, plan.DestinationPath, plan.IsDirectory, plan.ReplaceExisting);
            }
            else if (plan.IsDirectory)
            {
                CopyDirectory(boundary, plan.SourcePath, plan.DestinationPath);
            }
            else
            {
                File.Copy(plan.SourcePath, plan.DestinationPath, overwrite: plan.ReplaceExisting);
            }

            results.Add(new FileOperationResult(
                plan.SourcePath,
                plan.DestinationPath,
                plan.IsDirectory,
                plan.Mode,
                plan.ReplaceExisting));
        }

        return results;
    }

    public IReadOnlyList<FileOperationPlan> PlanImportCopy(
        string projectRoot,
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        FileConflictResolution conflictResolution = FileConflictResolution.Fail)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);

        var boundary = new PathBoundary(projectRoot);
        var destinationFolder = boundary.EnsureSafe(destinationDirectory);
        if (!Directory.Exists(destinationFolder))
        {
            throw new DirectoryNotFoundException("拖放目标不是可用的文件夹。");
        }

        var sources = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            throw new InvalidOperationException("外部拖入没有包含可复制的文件或文件夹。");
        }

        var plans = new List<FileOperationPlan>(sources.Length);
        var conflicts = new List<FileOperationConflict>();
        var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            var isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source))
            {
                throw new FileNotFoundException("外部拖入项目不存在。", source);
            }

            EnsureNoReparsePoints(source);
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            ValidateFileName(name);

            if (isDirectory && (PathsEqual(destinationFolder, source) || IsDescendant(destinationFolder, source)))
            {
                throw new InvalidOperationException("不能把文件夹复制到它自身或它的子文件夹中。");
            }

            var destination = boundary.EnsureSafe(Path.Combine(destinationFolder, name), mustExist: false);
            var destinationExists = PathExists(destination) || reservedDestinations.Contains(destination);
            if (destinationExists)
            {
                var conflict = new FileOperationConflict(source, destination, isDirectory);
                if (conflictResolution == FileConflictResolution.Fail)
                {
                    conflicts.Add(conflict);
                    continue;
                }

                if (conflictResolution == FileConflictResolution.Skip)
                {
                    continue;
                }

                if (conflictResolution == FileConflictResolution.KeepBoth)
                {
                    destination = GetUniqueDestination(boundary, destinationFolder, name, isDirectory, reservedDestinations);
                }
                else if (isDirectory)
                {
                    throw new InvalidOperationException("文件夹冲突暂不支持直接替换，请选择“保留两者”或“跳过”。");
                }
            }

            reservedDestinations.Add(destination);
            plans.Add(new FileOperationPlan(
                source,
                destination,
                isDirectory,
                FileTransferMode.Copy,
                ReplaceExisting: destinationExists && conflictResolution == FileConflictResolution.Replace));
        }

        if (conflicts.Count > 0)
        {
            throw new FileConflictException(conflicts);
        }

        return plans;
    }

    public IReadOnlyList<FileOperationResult> ImportCopy(
        string projectRoot,
        IEnumerable<string> sourcePaths,
        string destinationDirectory,
        FileConflictResolution conflictResolution = FileConflictResolution.Fail)
    {
        var boundary = new PathBoundary(projectRoot);
        var plans = PlanImportCopy(projectRoot, sourcePaths, destinationDirectory, conflictResolution);
        var results = new List<FileOperationResult>(plans.Count);

        foreach (var plan in plans)
        {
            if (plan.IsDirectory)
            {
                CopyDirectoryFromExternal(boundary, plan.SourcePath, plan.DestinationPath);
            }
            else
            {
                File.Copy(plan.SourcePath, plan.DestinationPath, overwrite: plan.ReplaceExisting);
            }

            results.Add(new FileOperationResult(
                plan.SourcePath,
                plan.DestinationPath,
                plan.IsDirectory,
                FileTransferMode.Copy,
                plan.ReplaceExisting));
        }

        return results;
    }

    public IReadOnlyList<string> PlanRecycle(string projectRoot, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var boundary = new PathBoundary(projectRoot);
        var sources = paths
            .Select(path => boundary.EnsureSafe(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sources.Length == 0)
        {
            throw new InvalidOperationException("没有选择要移到回收站的项目。");
        }

        foreach (var source in sources)
        {
            if (PathsEqual(source, boundary.RootPath))
            {
                throw new InvalidOperationException("不能删除当前项目根目录。");
            }
        }

        RejectNestedSourceSelection(sources);
        return sources;
    }

    public static void ValidateFileName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name is "." or "..")
        {
            throw new ArgumentException("名称不能是 . 或 ..。", nameof(name));
        }

        if (name.Length > 255)
        {
            throw new ArgumentException("名称不能超过 255 个字符。", nameof(name));
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("名称包含 Windows 不允许使用的字符。", nameof(name));
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            throw new ArgumentException("名称不能以空格或句点结尾。", nameof(name));
        }

        var deviceName = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(deviceName))
        {
            throw new ArgumentException("该名称是 Windows 保留的设备名。", nameof(name));
        }
    }

    private static void RejectNestedSourceSelection(IReadOnlyList<string> sources)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            if (!Directory.Exists(sources[index]))
            {
                continue;
            }

            for (var otherIndex = 0; otherIndex < sources.Count; otherIndex++)
            {
                if (index != otherIndex && IsDescendant(sources[otherIndex], sources[index]))
                {
                    throw new InvalidOperationException("不能同时操作一个文件夹和它内部的项目。");
                }
            }
        }
    }

    private static void CopyDirectory(PathBoundary boundary, string source, string destination)
    {
        boundary.EnsureSafe(source);
        boundary.EnsureSafe(destination, mustExist: false);
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var safeSource = boundary.EnsureSafe(file);
            var target = boundary.EnsureSafe(Path.Combine(destination, Path.GetFileName(file)), mustExist: false);
            File.Copy(safeSource, target, overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var safeSource = boundary.EnsureSafe(directory);
            var target = boundary.EnsureSafe(Path.Combine(destination, Path.GetFileName(directory)), mustExist: false);
            CopyDirectory(boundary, safeSource, target);
        }
    }

    private static void CopyDirectoryFromExternal(PathBoundary boundary, string source, string destination)
    {
        EnsureNoReparsePoints(source);
        boundary.EnsureSafe(destination, mustExist: false);
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            EnsureNoReparsePoints(file);
            var target = boundary.EnsureSafe(Path.Combine(destination, Path.GetFileName(file)), mustExist: false);
            File.Copy(file, target, overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            EnsureNoReparsePoints(directory);
            var target = boundary.EnsureSafe(Path.Combine(destination, Path.GetFileName(directory)), mustExist: false);
            CopyDirectoryFromExternal(boundary, directory, target);
        }
    }

    private static void RenameCaseOnly(string source, string destination, string parent, bool isDirectory)
    {
        var temporary = Path.Combine(parent, $".project-file-hub-rename-{Guid.NewGuid():N}.tmp");
        MovePath(source, temporary, isDirectory);

        try
        {
            MovePath(temporary, destination, isDirectory);
        }
        catch
        {
            MovePath(temporary, source, isDirectory);
            throw;
        }
    }

    private static void EnsureDestinationAvailable(string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("目标位置已存在同名文件或文件夹。");
        }
    }

    private static void MovePath(string source, string destination, bool isDirectory, bool replaceExisting = false)
    {
        if (isDirectory)
        {
            if (replaceExisting)
            {
                throw new InvalidOperationException("文件夹冲突暂不支持直接替换。");
            }

            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: replaceExisting);
        }
    }

    private static string GetUniqueDestination(
        PathBoundary boundary,
        string destinationFolder,
        string originalName,
        bool isDirectory,
        IReadOnlySet<string> reservedDestinations)
    {
        var extension = isDirectory ? string.Empty : Path.GetExtension(originalName);
        var baseName = isDirectory ? originalName : Path.GetFileNameWithoutExtension(originalName);

        for (var suffix = 2; suffix <= 10_000; suffix++)
        {
            var name = $"{baseName} ({suffix}){extension}";
            var candidate = boundary.EnsureSafe(Path.Combine(destinationFolder, name), mustExist: false);
            if (!PathExists(candidate) && !reservedDestinations.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("无法为同名项目生成可用名称。");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Directory.Exists(fullPath)
            ? new DirectoryInfo(fullPath)
            : File.Exists(fullPath)
                ? new FileInfo(fullPath).Directory
                : null;

        while (current is not null)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("外部拖入路径包含符号链接或目录联接。");
            }

            current = current.Parent;
        }

        if (File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("外部拖入文件是符号链接或重解析点。");
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsDescendant(string candidate, string directory)
    {
        var directoryPath = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidatePath = Path.GetFullPath(candidate);
        return candidatePath.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase);
    }
}
