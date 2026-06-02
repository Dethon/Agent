namespace Domain.Tools.FileSystem;

// Expands brace alternations (`{a,b,c}`) into the equivalent flat list of glob patterns, e.g.
// `**/*.{jpg,png}` -> [`**/*.jpg`, `**/*.png`]. Groups nest and combine as a cartesian product.
// A brace group only expands when it contains a top-level comma; lone `{...}` and unbalanced
// braces are left literal so existing patterns keep matching unchanged.
public static class GlobBraceExpander
{
    public static IReadOnlyList<string> Expand(string pattern)
    {
        if (!TryFindGroup(pattern, out var open, out var close))
        {
            return [pattern];
        }

        var prefix = pattern[..open];
        var body = pattern[(open + 1)..close];
        var suffixExpansions = Expand(pattern[(close + 1)..]);

        return (
            from alternative in SplitTopLevel(body)
            from expandedAlternative in Expand(alternative)
            from suffix in suffixExpansions
            select prefix + expandedAlternative + suffix
        ).ToList();
    }

    // Locates the first '{' that is balanced and holds a top-level comma. Braces that are
    // unbalanced or comma-free are skipped (treated as literals), matching shell behaviour.
    private static bool TryFindGroup(string pattern, out int open, out int close)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '{' && TryScanGroup(pattern, i, out close))
            {
                open = i;
                return true;
            }
        }

        open = close = -1;
        return false;
    }

    // Scans the brace group opening at `open`. Succeeds only when the group closes and holds a
    // top-level comma; comma-free or unbalanced braces fail so they stay literal.
    private static bool TryScanGroup(string pattern, int open, out int close)
    {
        var depth = 0;
        var hasTopLevelComma = false;
        for (var j = open; j < pattern.Length; j++)
        {
            switch (pattern[j])
            {
                case '{':
                    depth++;
                    break;
                case ',' when depth == 1:
                    hasTopLevelComma = true;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        close = j;
                        return hasTopLevelComma;
                    }

                    break;
            }
        }

        close = -1;
        return false;
    }

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(body[start..i]);
                    start = i + 1;
                    break;
            }
        }

        parts.Add(body[start..]);
        return parts;
    }
}