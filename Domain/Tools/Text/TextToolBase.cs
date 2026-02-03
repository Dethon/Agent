namespace Domain.Tools.Text;

public abstract class TextToolBase(string vaultPath, string[] allowedExtensions)
{
    protected string VaultPath => vaultPath;
    protected string[] AllowedExtensions => allowedExtensions;

    protected string ValidateAndResolvePath(string filePath)
    {
        var fullPath = Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : Path.GetFullPath(Path.Combine(vaultPath, filePath));

        if (!fullPath.StartsWith(vaultPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Access denied: path must be within vault directory");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
        {
            throw new InvalidOperationException(
                $"File type '{ext}' not allowed. Allowed: {string.Join(", ", allowedExtensions)}");
        }

        return fullPath;
    }
}