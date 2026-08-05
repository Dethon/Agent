using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

// Shared line-oriented content search for virtual filesystem backends (Home Assistant, scheduling)
// so fs_search behaves identically across filesystems: regex/literal matching, per-file match
// capping with truncation, context lines, and Content vs FilesOnly output shape.
internal static class VfsContentSearch
{
    public static (List<FsSearchMatch> Matches, bool More) FindMatches(
        string[] lines, Regex matcher, int contextLines, int limit)
    {
        var hits = lines
            .Select((text, index) => (text, index))
            .Where(l => matcher.IsMatch(l.text))
            .ToList();
        var taken = hits
            .Take(limit)
            .Select(l => BuildMatch(lines, l.index, contextLines))
            .ToList();
        return (taken, hits.Count > limit);
    }

    public static FsSearchFileResult BuildFileResult(
        string file, IReadOnlyList<FsSearchMatch> matches, VfsTextSearchOutputMode outputMode) =>
        outputMode == VfsTextSearchOutputMode.FilesOnly
            ? new FsSearchFileResult { File = file, MatchCount = matches.Count }
            : new FsSearchFileResult { File = file, Matches = matches };

    // Matches a bare file name against a simple glob (* and ?). Used to gate which file(s) a backend
    // exposes as searchable against a caller-supplied filePattern.
    //
    // The pattern is the caller's, so it is as untrusted as the query and gets the same two
    // guards: compiled once under a bounded match timeout — `*a*a*a…b` over a long file name
    // backtracks past any deadline otherwise — and a pattern that cannot compile answers the
    // invalid-argument envelope instead of throwing out of fs_search.
    public static FsResult<Func<string, bool>> CompileFilePattern(string? filePattern, TimeSpan matchTimeout)
    {
        if (string.IsNullOrEmpty(filePattern))
        {
            return new FsResult<Func<string, bool>>.Ok(_ => true);
        }

        var pattern = "^" + Regex.Escape(filePattern).Replace("\\*", "[^/]*").Replace("\\?", ".") + "$";
        try
        {
            var matcher = new Regex(pattern, RegexOptions.IgnoreCase, matchTimeout);
            return new FsResult<Func<string, bool>>.Ok(matcher.IsMatch);
        }
        catch (ArgumentException ex)
        {
            return FsError.Invalid<Func<string, bool>>($"Invalid filePattern '{filePattern}': {ex.Message}");
        }
    }

    private static FsSearchMatch BuildMatch(string[] lines, int index, int contextLines)
    {
        if (contextLines <= 0)
        {
            return new FsSearchMatch { Line = index + 1, Text = lines[index] };
        }
        var before = lines.Take(index).TakeLast(contextLines).ToList();
        var after = lines.Skip(index + 1).Take(contextLines).ToList();
        var hasContext = before.Count > 0 || after.Count > 0;
        return new FsSearchMatch
        {
            Line = index + 1,
            Text = lines[index],
            Context = hasContext ? new FsSearchContext { Before = before, After = after } : null
        };
    }
}