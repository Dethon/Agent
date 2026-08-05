using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

// The two glob conventions every mount honours, applied to a caller's pattern: basePath scopes the
// pattern, and a trailing slash asks for directories only. Candidates handed to Matches are
// mount-root-relative, without a leading slash. Creation answers the invalid-argument envelope
// instead of throwing, so a pattern that cannot be compiled (the brace-expansion cap) reaches the
// caller as the standard error on every backend rather than as a raw exception.
public sealed record GlobScope(bool DirsOnly, Func<string, bool> Matches)
{
    public static FsResult<GlobScope> Create(string? basePath, string pattern)
    {
        var prefix = string.IsNullOrEmpty(basePath?.Trim('/')) ? string.Empty : basePath.Trim('/') + "/";
        var dirsOnly = pattern.EndsWith('/');
        var effectivePattern = dirsOnly ? pattern.TrimEnd('/') : pattern;
        try
        {
            return new FsResult<GlobScope>.Ok(
                new GlobScope(dirsOnly, GlobRegex.CompileMatcher(prefix + effectivePattern)));
        }
        catch (ArgumentException ex)
        {
            return FsError.Invalid<GlobScope>(ex.Message);
        }
    }
}