using System.Text.RegularExpressions;

namespace Infrastructure.Utils;

public sealed class ToolPatternMatcher
{
    private readonly List<Regex> _patterns;

    public ToolPatternMatcher(IEnumerable<string>? patterns)
    {
        _patterns = (patterns ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(ConvertToRegex)
            .ToList();
    }

    public bool IsMatch(string qualifiedToolName)
    {
        ArgumentNullException.ThrowIfNull(qualifiedToolName);
        return _patterns.Any(p => p.IsMatch(qualifiedToolName));
    }

    private static Regex ConvertToRegex(string pattern)
    {
        // Escape regex special chars except *
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*");
        return new Regex($"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}