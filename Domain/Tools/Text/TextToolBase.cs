using Domain.Tools.Files;

namespace Domain.Tools.Text;

public abstract class TextToolBase(string vaultPath, string[] allowedExtensions)
{
    protected PathJail Jail { get; } = new(vaultPath);

    protected string VaultPath => vaultPath;
    protected string[] AllowedExtensions => allowedExtensions;

    protected string ValidateAndResolvePath(string filePath)
    {
        var fullPath = Jail.Resolve(filePath);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            throw new ArgumentException(
                $"File type '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }

        return fullPath;
    }
}