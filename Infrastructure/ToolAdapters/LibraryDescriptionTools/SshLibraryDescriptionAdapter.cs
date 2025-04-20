using Domain.Tools;
using Renci.SshNet;

namespace Infrastructure.ToolAdapters.LibraryDescriptionTools;

public class SshLibraryDescriptionAdapter(SshClient client, string baseLibraryPath) : LibraryDescriptionTool
{
    protected override LibraryDescriptionNode Resolve()
    {
        if (!client.IsConnected)
        {
            client.Connect();
        }

        try
        {
            var checkDirCommand = $"[ -d \"{baseLibraryPath}\" ] && echo \"EXISTS\" || echo \"NOT_EXISTS\"";
            var checkResult = client.RunCommand(checkDirCommand).Result.Trim();
            if (checkResult != "EXISTS")
            {
                throw new DirectoryNotFoundException($"Library directory not found: {baseLibraryPath}");
            }

            return new LibraryDescriptionNode
            {
                Name = baseLibraryPath,
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(baseLibraryPath)
            };
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private LibraryDescriptionNode[]? GetLibraryChildNodes(string basePath)
    {
        var dirs = client.RunCommand($"find \"{basePath}\" -maxdepth 1 -type d -not -path \"{basePath}\"")
            .Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = client.RunCommand($"find \"{basePath}\" -maxdepth 1 -type f")
            .Result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var fileNodes = files
            .Select(file => new LibraryDescriptionNode
            {
                Name = Path.GetFileName(file),
                Type = LibraryEntryType.File
            });

        var nodes = dirs
            .Where(dir => dir != basePath)
            .Select(dir => new LibraryDescriptionNode
            {
                Name = Path.GetFileName(dir),
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(dir)
            })
            .Concat(fileNodes)
            .ToArray();
        return nodes.Length > 0 ? nodes : null;
    }
}