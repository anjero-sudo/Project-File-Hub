using Microsoft.VisualBasic.FileIO;
using ProjectFileHub.Core.Services;

namespace ProjectFileHub.App.WindowsIntegration;

internal sealed record RecycledItem(string OriginalPath, bool IsDirectory);

internal sealed class RecycleBinService
{
    private readonly FileOperationService _fileOperations;

    public RecycleBinService(FileOperationService fileOperations)
    {
        _fileOperations = fileOperations;
    }

    public IReadOnlyList<RecycledItem> MoveToRecycleBin(string projectRoot, IEnumerable<string> paths)
    {
        var planned = _fileOperations.PlanRecycle(projectRoot, paths);
        var results = new List<RecycledItem>(planned.Count);

        foreach (var path in planned)
        {
            var isDirectory = Directory.Exists(path);
            if (isDirectory)
            {
                FileSystem.DeleteDirectory(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }
            else
            {
                FileSystem.DeleteFile(
                    path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }

            results.Add(new RecycledItem(path, isDirectory));
        }

        return results;
    }
}
