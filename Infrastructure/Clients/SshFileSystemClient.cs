using Domain.Contracts;
using Infrastructure.Wrappers;
using Renci.SshNet.Common;

namespace Infrastructure.Clients;

public class SshFileSystemClient(ISshClientWrapper client) : IFileSystemClient
{
    private readonly Lock _lLock = new();

    public Task<string[]> DescribeDirectory(string path)
    {
        return ConnectionWrapper(() =>
        {
            if (!DoesFolderExist(path))
            {
                throw new DirectoryNotFoundException($"Library directory not found: {path}");
            }

            return Task.FromResult(GetLibraryPaths(path));
        });
    }

    public Task Move(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        return ConnectionWrapper(() =>
        {
            if (!DoesFileExist(sourcePath) && !DoesFolderExist(sourcePath))
            {
                throw new IOException("Source file does not exist");
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
            throw new SshException($"{sshCommand.Error}");
        }
    }

    private string[] GetLibraryPaths(string basePath)
    {
        return client.RunCommand($"find \"{basePath}\" -type f").Result
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToLookup(x => Path.GetDirectoryName(x)?.Replace('\\', '/') ?? string.Empty, x => x)
            .Where(x => x.Key != string.Empty)
            .SelectMany(x => x.Take(3))
            .ToArray();
    }

    private void CreateDestinationParentPath(string destinationPath)
    {
        var parentPath = Path.GetDirectoryName(destinationPath)?.Replace('\\', '/');
        if (string.IsNullOrEmpty(parentPath) || DoesFolderExist(parentPath) || DoesFileExist(parentPath))
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