using System.Globalization;
using System.Text;

namespace Domain.Tools.HomeAssistant.Vfs;

// Renders the human-readable directory segment `<id>_(<name-slug>)` and recovers the id from it.
// An HA entity_id / object_id (charset [a-z0-9_.]) never contains '(', so the first "_(" is always
// the delimiter — StripNice keys on it and ignores the decorative suffix, making the round-trip safe
// even for adversarial friendly names. Slugifying the name also guarantees it cannot contain the
// delimiter characters in the first place.
public static class HaSlug
{
    private const int MaxLength = 60;
    private const string Delimiter = "_(";

    public static string Compose(string id, string? friendlyName)
    {
        var slug = Slugify(friendlyName);
        return slug.Length == 0 ? id : $"{id}{Delimiter}{slug})";
    }

    public static string StripNice(string segment)
    {
        var i = segment.IndexOf(Delimiter, StringComparison.Ordinal);
        return i < 0 ? segment : segment[..i];
    }

    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var chars = name.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length <= MaxLength ? slug : TrimToWord(slug);
    }

    private static string TrimToWord(string slug)
    {
        var cut = slug[..MaxLength];
        var lastDash = cut.LastIndexOf('-');
        return lastDash > 0 ? cut[..lastDash] : cut;
    }
}