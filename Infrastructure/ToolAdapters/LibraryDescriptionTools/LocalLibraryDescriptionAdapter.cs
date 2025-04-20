using Domain.Tools;

namespace Infrastructure.ToolAdapters.LibraryDescriptionTools;

public class LocalLibraryDescriptionAdapter(string baseLibraryPath) : LibraryDescriptionTool
{
    protected override LibraryDescriptionNode Resolve()
    {
        if (!Directory.Exists(baseLibraryPath))
        {
            throw new DirectoryNotFoundException($"Library directory not found: {baseLibraryPath}");
        }

        return new LibraryDescriptionNode
        {
            Name = Path.GetFileName(baseLibraryPath),
            Type = LibraryEntryType.Directory,
            Children = GetLibraryChildNodes(baseLibraryPath)
        };
    }

    private static LibraryDescriptionNode[] GetLibraryChildNodes(string basePath)
    {
        var fileNodes = Directory.GetFiles(basePath)
            .Select(file => new LibraryDescriptionNode
            {
                Name = Path.GetFileName(file),
                Type = LibraryEntryType.File
            });
        return Directory.GetDirectories(basePath)
            .Select(directory => new LibraryDescriptionNode
            {
                Name = Path.GetDirectoryName(directory) ??
                       throw new DirectoryNotFoundException($"Directory name not found: {directory}"),
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(directory)
            })
            .Concat(fileNodes)
            .ToArray();
    }
}