using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public static class FileCategoryClassifier
{
    public static FileItemCategory Classify(string extension, bool isDirectory)
    {
        if (isDirectory)
        {
            return FileItemCategory.Folder;
        }

        return FileFormatCatalog.TryGet(extension, out var descriptor)
            ? descriptor.Category
            : FileItemCategory.Other;
    }
}
