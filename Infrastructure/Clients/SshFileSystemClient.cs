using Domain.Contracts;
using Domain.DTOs;
using Renci.SshNet;

namespace Infrastructure.Clients;

public class SshFileSystemClient(SshClient client) : IFileSystemClient
{
    public Task<LibraryDescriptionNode> DescribeDirectory(string path)
    {
        if (!client.IsConnected)
        {
            client.Connect();
        }

        try
        {
            var checkDirCommand = $"[ -d \"{path}\" ] && echo \"EXISTS\" || echo \"NOT_EXISTS\"";
            var checkResult = client.RunCommand(checkDirCommand).Result.Trim();
            if (checkResult != "EXISTS")
            {
                throw new DirectoryNotFoundException($"Library directory not found: {path}");
            }

            return Task.FromResult(new LibraryDescriptionNode
            {
                Name = path,
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(path)
            });
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    public Task Move(string sourceFile, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (!client.IsConnected)
        {
            client.Connect();
        }

        try
        {
            if (!DoesFileExist(sourceFile))
            {
                throw new Exception("Source file does not exist");
            }

            CreateDestinationPath(destinationPath);
            MoveFile(sourceFile, destinationPath);
            return Task.CompletedTask;
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

    private void MoveFile(string sourceFile, string destinationPath)
    {
        var moveCommand = client.CreateCommand($"mv \"{sourceFile}\" \"{destinationPath}\"");
        moveCommand.Execute();
        if (!string.IsNullOrEmpty(moveCommand.Error))
        {
            throw new Exception($"Failed to move file: {moveCommand.Error}");
        }
    }

    private void CreateDestinationPath(string destinationPath)
    {
        if (DoesFolderExist(destinationPath))
        {
            return;
        }

        var createDirCommand = client.RunCommand($"mkdir -p \"{destinationPath}\"");
        createDirCommand.Execute();
        if (!string.IsNullOrEmpty(createDirCommand.Error))
        {
            throw new Exception($"Failed to create destination directory: {createDirCommand.Error}");
        }
    }

    private bool DoesFolderExist(string path)
    {
        return DoesPathExist(path, 'd');
    }

    private bool DoesFileExist(string path)
    {
        return DoesPathExist(path, 'f');
    }

    private bool DoesPathExist(string path, char descriptor)
    {
        var checkDirCommand = $"[ -{descriptor} \"{path}\" ] && echo \"EXISTS\" || echo \"NOT_EXISTS\"";
        var dirExists = client.RunCommand(checkDirCommand).Result.Trim();
        return dirExists == "EXISTS";
    }
}