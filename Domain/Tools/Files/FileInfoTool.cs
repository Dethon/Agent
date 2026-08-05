using Domain.DTOs.FileSystem;

namespace Domain.Tools.Files;

public class FileInfoTool(string rootPath)
{
    private readonly PathJail _jail = new(rootPath);

    protected const string Description = """
                                         Returns metadata about a path: exists, isDirectory, size (files only), and lastModified.
                                         Use as a cheap guard before read/edit/move/delete to avoid errors on missing paths.
                                         Works for files and directories; never fails on missing paths — returns exists=false instead.
                                         """;

    public FsResult<FsInfoResult> Run(string path)
    {
        if (!_jail.TryResolve(path, out var fullPath))
        {
            return FsError.Invalid<FsInfoResult>(_jail.DeniedMessage);
        }

        if (File.Exists(fullPath))
        {
            var info = new FileInfo(fullPath);
            return Ok(new FsInfoResult
            {
                Exists = true,
                Path = fullPath,
                IsDirectory = false,
                Size = info.Length,
                LastModified = info.LastWriteTimeUtc.ToString("O")
            });
        }

        if (Directory.Exists(fullPath))
        {
            return Ok(new FsInfoResult
            {
                Exists = true,
                Path = fullPath,
                IsDirectory = true,
                LastModified = new DirectoryInfo(fullPath).LastWriteTimeUtc.ToString("O")
            });
        }

        return Ok(new FsInfoResult { Exists = false, Path = fullPath });
    }

    private static FsResult<FsInfoResult> Ok(FsInfoResult value) => new FsResult<FsInfoResult>.Ok(value);
}