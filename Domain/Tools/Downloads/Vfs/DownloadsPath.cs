using System.Globalization;

namespace Domain.Tools.Downloads.Vfs;

public enum DownloadNodeKind
{
    DownloadDir,
    StatusFile,
    Other
}

public sealed record DownloadsNode(DownloadNodeKind Kind, int? Id);

// Classifies media-filesystem paths against the downloads overlay: downloads/<id> is a
// download directory and downloads/<id>/status.json its virtual status file; everything
// else (including payload files inside a download directory) is plain disk territory.
public static class DownloadsPath
{
    public const string StatusFileName = "status.json";

    public static DownloadsNode Parse(string path) =>
        Canonicalize(path)?.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
        {
            [MediaFilesystem.DownloadsSubdir, var id] when TryParseId(id, out var dirId) =>
                new DownloadsNode(DownloadNodeKind.DownloadDir, dirId),
            [MediaFilesystem.DownloadsSubdir, var id, StatusFileName] when TryParseId(id, out var fileId) =>
                new DownloadsNode(DownloadNodeKind.StatusFile, fileId),
            _ => new DownloadsNode(DownloadNodeKind.Other, null)
        };

    // The spelling the disk underneath will end up at: '.' dropped and '..' resolved, exactly as
    // Path.GetFullPath does inside PathJail. Classifying the caller's literal spelling instead left
    // every refusal on this mount one '.' away from being switched off — 'downloads/42/./status.json'
    // matched nothing, so the disk wrote a real file where the virtual status.json is, invisible
    // afterwards and unremovable. Null means the path climbs above the mount root: not the overlay's
    // territory, and the jail refuses it anyway.
    public static string? Canonicalize(string? path)
    {
        var canonical = new List<string>();
        foreach (var segment in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment != "..")
            {
                canonical.Add(segment);
            }
            else if (canonical.Count == 0)
            {
                return null;
            }
            else
            {
                canonical.RemoveAt(canonical.Count - 1);
            }
        }

        return string.Join('/', canonical);
    }

    // The overlay owns exactly the ids the download manager hands out: an int spelled the way
    // int.ToString spells it — a minus for negatives (ids are link hash codes, so half are), no
    // plus, no padding, no surrounding blanks. A directory literally named '042', '+42' or ' 42 '
    // is a real directory on disk, and shadowing it with a virtual status file — or cancelling
    // download 42 when asked to delete it — is not the overlay's call. Round-tripping the parse is
    // the whole rule.
    private static bool TryParseId(string segment, out int id) =>
        int.TryParse(segment, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id)
        && id.ToString(CultureInfo.InvariantCulture) == segment;
}