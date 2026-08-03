using System.Text.Json.Nodes;
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

    protected JsonNode Run(string filePath, string content, bool overwrite = false, bool createDirectories = true)
    {
        var fullPath = _jail.Resolve(filePath);
        ValidateExtension(fullPath);
        if (!overwrite)
        {
            ValidateNotExists(fullPath, filePath);
        }

        if (createDirectories)
        {
            EnsureDirectoryExists(fullPath);
        }

        File.WriteAllText(fullPath, content);

        var info = new FileInfo(fullPath);
        return FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created",
            FilePath = ToRelativePath(fullPath),
            Size = FormatFileSize(info.Length),
            Lines = content.Split('\n').Length
        });
    }

    private void ValidateExtension(string fullPath)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            throw new ArgumentException(
                $"File extension '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }
    }

    private static void ValidateNotExists(string fullPath, string originalPath)
    {
        if (File.Exists(fullPath))
        {
            throw new IOException(
                $"File already exists: {originalPath}. Use the edit tool to modify existing files.");
        }
    }

    private static void EnsureDirectoryExists(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private string ToRelativePath(string fullPath)
    {
        return Path.GetRelativePath(vaultPath, fullPath).Replace('\\', '/');
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
        };
    }
}