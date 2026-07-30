using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.MusicAssistant;

namespace Domain.Tools.HomeAssistant.Vfs;

// Implements the `music_assistant.podcast_episodes` action (see HaMusicActions). Resolves the show
// — by name or by uri — then returns its episodes with the uri each one plays by.
internal static class HaPodcastEpisodes
{
    private const int ShowSearchLimit = 5;

    public static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        IMusicAssistantClient? client, JsonObject data, CancellationToken ct)
    {
        if (client is null)
        {
            return (1, "", "Music Assistant is not configured on this server, so podcast episodes cannot be listed.");
        }

        if (data["podcast"]?.GetValue<string>() is not { } podcast || string.IsNullOrWhiteSpace(podcast))
        {
            return (2, "", "Missing required argument '--podcast' (the show's name, or its Music Assistant uri).");
        }

        var limit = data["limit"]?.GetValue<int>() ?? HaMusicActions.DefaultEpisodeLimit;
        var match = data["match"]?.GetValue<string>();

        try
        {
            var show = await ResolveShowAsync(client, podcast, ct);
            if (show is null)
            {
                return (1, "", $"No podcast named '{podcast}' found. Search the exact show name first, then retry.");
            }

            var episodes = await client.GetPodcastEpisodesAsync(show.Uri, ct);
            var filtered = Filter(episodes, match);
            var shown = filtered.Take(limit).ToList();

            return (0, Render(show, episodes.Count, filtered.Count, shown, match), "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (1, "", ex.Message);
        }
    }

    private sealed record ResolvedShow(string Name, MaUri Uri);

    // A uri argument is used verbatim; anything else is a name that has to be searched, because the
    // caller cannot know a provider's item id for a show.
    private static async Task<ResolvedShow?> ResolveShowAsync(
        IMusicAssistantClient client, string podcast, CancellationToken ct)
    {
        if (MaUri.LooksLikeUri(podcast))
        {
            return MaUri.TryParse(podcast, out var parsed) ? new ResolvedShow(podcast, parsed) : null;
        }

        var hits = await client.SearchAsync(podcast, "podcast", ShowSearchLimit, ct);
        var best = hits.FirstOrDefault(h => Normalize(h.Name) == Normalize(podcast)) ?? hits.FirstOrDefault();
        return best is not null && MaUri.TryParse(best.Uri, out var uri) ? new ResolvedShow(best.Name, uri) : null;
    }

    private static IReadOnlyList<MaMediaItem> Filter(IReadOnlyList<MaMediaItem> episodes, string? match) =>
        string.IsNullOrWhiteSpace(match)
            ? episodes
            : episodes.Where(e => Normalize(e.Name).Contains(Normalize(match), StringComparison.Ordinal)).ToList();

    private static string Render(
        ResolvedShow show, int total, int matched, IReadOnlyList<MaMediaItem> shown, string? match)
    {
        var payload = new JsonObject
        {
            ["ok"] = true,
            ["podcast"] = new JsonObject { ["name"] = show.Name, ["uri"] = show.Uri.ToString() },
            ["total"] = total,
            ["shown"] = shown.Count,
            ["truncated"] = matched > shown.Count,
            ["episodes"] = new JsonArray(shown.Select(Episode).ToArray())
        };

        if (matched > shown.Count)
        {
            payload["suggestion"] = $"{matched} episodes matched. Narrow it with --match, or raise --limit.";
        }
        else if (shown.Count == 0)
        {
            payload["suggestion"] = match is null
                ? "This podcast reported no episodes."
                : $"No episode title contains '{match}'. Try fewer words, or drop --match to see the titles.";
        }
        return payload.ToJsonString();
    }

    private static JsonNode Episode(MaMediaItem episode)
    {
        var node = new JsonObject { ["title"] = episode.Name, ["uri"] = episode.Uri };
        if (episode.DurationSeconds is { } duration)
        {
            node["durationSeconds"] = (int)duration;
        }
        return node;
    }

    // Voice transcripts arrive without accents and in arbitrary case, and Spanish episode titles are
    // full of them, so both sides are folded before comparing.
    private static string Normalize(string value) =>
        new string(value.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray())
            .Normalize(NormalizationForm.FormC)
            .ToLowerInvariant();
}