using Domain.DTOs.FileSystem;
using Domain.Tools.Files;

namespace Domain.Tools.Text;

public abstract class TextToolBase(string vaultPath, string[] allowedExtensions)
{
    protected PathJail Jail { get; } = new(vaultPath);

    protected string VaultPath => vaultPath;
    protected string[] AllowedExtensions => allowedExtensions;

    // Resolves a caller path to an existing file of an allowed type, or says which of those three
    // things went wrong. Every text tool starts here, so they all reject the same way.
    protected FsResult<string> ResolveExistingFile(string filePath)
    {
        if (!Jail.TryResolve(filePath, out var fullPath))
        {
            return FsError.Invalid<string>(Jail.DeniedMessage);
        }

        if (!File.Exists(fullPath))
        {
            return FsError.NotFound<string>(filePath);
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return allowedExtensions.Contains(ext)
            ? new FsResult<string>.Ok(fullPath)
            : FsError.Invalid<string>(
                $"File type '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
    }
}