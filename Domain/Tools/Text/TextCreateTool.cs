using Domain.DTOs.FileSystem;
using Domain.Tools.Files;

namespace Domain.Tools.Text;

public class TextCreateTool(string vaultPath, string[] allowedExtensions)
{
    private readonly PathJail _jail = new(vaultPath);

    protected const string Description = """
                                         Creates a new text or markdown file in the vault.

                                         Use this to create new notes, documentation, or configuration files.
                                         The file must not already exist and must have an allowed extension.

                                         Parameters:
                                         - filePath: Path for the new file (relative to vault or absolute)
                                         - content: Initial content for the file
                                         - createDirectories: Create parent directories if they don't exist (default: true)

                                         Examples:
                                         - Create a note: filePath="notes/new-topic.md", content="# New Topic\n\nContent here..."
                                         - Create config: filePath="config/settings.json", content="{\"key\": \"value\"}"
                                         """;

    public FsResult<FsCreateResult> Run(string filePath, string content, bool overwrite = false, bool createDirectories = true)
    {
        if (!_jail.TryResolve(filePath, out var fullPath))
        {
            return FsError.Invalid<FsCreateResult>(_jail.DeniedMessage);
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            return FsError.Invalid<FsCreateResult>(
                $"File extension '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }

        if (!overwrite && File.Exists(fullPath))
        {
            return FsError.AlreadyExists<FsCreateResult>(
                $"File already exists: {filePath}. Use the edit tool to modify existing files.");
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (!createDirectories)
            {
                return FsError.NotFound<FsCreateResult>(directory);
            }

            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "created",
            FilePath = Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/'),
            Size = FormatFileSize(new FileInfo(fullPath).Length),
            Lines = content.Split('\n').Length
        });
    }

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };
}