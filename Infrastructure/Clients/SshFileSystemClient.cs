using Domain.Contracts;
using Domain.DTOs;
using Renci.SshNet;
using Renci.SshNet.Common;

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

            CreateDestinationParentPath(destinationPath);
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
            throw new SshException($"Failed to to execute {command}: {sshCommand.Error}");
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

    private void CreateDestinationParentPath(string destinationPath)
    {
        var parentPath = Path.GetDirectoryName(destinationPath);
        if (DoesFolderExist(destinationPath) || DoesFileExist(destinationPath) || string.IsNullOrEmpty(parentPath))
        {
            return;
        }

        RunCommand($"umask 002 && mkdir -p \"{parentPath}\" && umask 022");
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