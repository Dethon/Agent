using System.Text;
using System.Text.RegularExpressions;

namespace Domain.Tools.FileSystem;

// Translates hierarchical glob patterns into matchers over '/'-separated virtual paths, shared by
// the non-disk VFS backends (Home Assistant, schedules) so their glob semantics stay identical.
// `*` matches one path segment, `?` one character, `**` recurses. The `**/` form matches zero or
// more whole segments, so `**/X` also matches X at the base level — mirroring the Local file
// matcher rather than requiring a leading '/'. Brace alternation (`{a,b}`) is expanded first via
// GlobBraceExpander, so a path matches when any expanded alternative matches.
public static class GlobRegex
{
    // The segment separator inside the `(?:[^/]+/)*` group can't overlap '[^/]+', so the emitted
    // patterns can't catastrophically backtrack; this timeout is belt-and-suspenders only.
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromSeconds(1);

    public static Func<string, bool> CompileMatcher(string pattern)
    {
        var regexes = GlobBraceExpander.Expand(pattern).Select(Compile).ToList();
        return path => regexes.Any(r => r.IsMatch(path));
    }

    private static Regex Compile(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // `**/` matches zero or more whole segments (incl. none); bare `**` matches anything.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:[^/]+/)*");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i++;
                }
            }
            else
            {
                sb.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString())
                });
            }
        }

        sb.Append('$');
        // Glob regexes are compiled fresh per call and matched once over a small pool, so the
        // interpreter is cheaper than RegexOptions.Compiled's JIT cost.
        return new Regex(sb.ToString(), RegexOptions.None, _matchTimeout);
    }
}