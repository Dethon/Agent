namespace Domain.Tools.MusicAssistant;

// A Music Assistant item URI: `<provider>://<media_type>/<item_id>`, e.g.
// `spotify--w2nq2jMe://podcast_episode/4Fk1sWv0xKvJ6teiCpTAJN`. The provider segment is a provider
// *instance* id (`spotify--w2nq2jMe`) when several instances of a provider are configured and a bare
// provider domain (`library`) otherwise; MA accepts either wherever it takes
// `provider_instance_id_or_domain`, so we keep whatever the URI carried.
public readonly record struct MaUri(string Provider, string MediaType, string ItemId)
{
    private const string Separator = "://";

    public override string ToString() => $"{Provider}{Separator}{MediaType}/{ItemId}";

    // Cheap discriminator for arguments that accept either a free-text title or a URI. Matching on
    // `://` (not a bare `:`) keeps an episode title like "280. Palantir: el control..." — and
    // Spotify's own `spotify:episode:<id>` form, which MA does NOT accept — out of the URI branch.
    public static bool LooksLikeUri(string? candidate) =>
        candidate?.Contains(Separator, StringComparison.Ordinal) == true;

    // Only the first two separators are structural: a file-backed provider's item id is a relative
    // path and keeps its own slashes.
    public static bool TryParse(string? candidate, out MaUri uri)
    {
        uri = default;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var schemeEnd = candidate.IndexOf(Separator, StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var provider = candidate[..schemeEnd];
        var rest = candidate[(schemeEnd + Separator.Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        uri = new MaUri(provider, rest[..slash], rest[(slash + 1)..]);
        return true;
    }
}