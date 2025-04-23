using Domain.Contracts;
using Domain.DTOs;
using Renci.SshNet;

namespace Infrastructure.Clients;

public class SshFileSystemClient(SshClient client) : IFileSystemClient
{
    private readonly Lock _lLock = new();

    public Task<LibraryDescriptionNode> DescribeDirectory(string path)
    {
        return ConnectionWrapper(() =>
        {
            if (!DoesFolderExist(path))
            {
                throw new DirectoryNotFoundException($"Library directory not found: {path}");
            }

            return Task.FromResult(new LibraryDescriptionNode
            {
                Name = path,
                Type = LibraryEntryType.Directory,
                Children = GetLibraryChildNodes(path)
            });
        });
    }

    public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        return ConnectionWrapper(() =>
        {
            if (!DoesFileExist(sourcePath) && !DoesFolderExist(sourcePath))
            {
                throw new Exception("Source file does not exist");
            }

            CreateDestinationPath(destinationPath);
            RunCommand($"mv -T \"{sourcePath}\" \"{destinationPath}\"");
            return Task.CompletedTask;
        });
    }

    public Task RemoveDirectory(string path, CancellationToken cancellationToken = default)
    {
        return ConnectionWrapper(() =>
        {
            RunCommand($"rm -rf \"{path}\"");
            return Task.CompletedTask;
        });
    }

    public Task RemoveFile(string path, CancellationToken cancellationToken = default)
    {
        return ConnectionWrapper(() =>
        {
            RunCommand($"rm -f \"{path}\"");
            return Task.CompletedTask;
        });
    }

    private T ConnectionWrapper<T>(Func<T> action)
    {
        lock (_lLock)
        {
            if (!client.IsConnected)
            {
                client.Connect();
            }

            try
            {
                return action();
            }
            finally
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }
        }
    }

    private void RunCommand(string command)
    {
        var sshCommand = client.CreateCommand(command);
        sshCommand.Execute();
        if (!string.IsNullOrEmpty(sshCommand.Error))
        {
            throw new Exception($"Failed to to execute {command} file: {sshCommand.Error}");
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

    private void CreateDestinationPath(string destinationPath)
    {
        if (DoesFolderExist(destinationPath) || DoesFileExist(destinationPath))
        {
            return;
        }

        var createDirCommand = client.RunCommand($"mkdir -m775 -p \"{destinationPath}\"");
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