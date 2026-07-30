using Domain.Tools.MusicAssistant;
using JetBrains.Annotations;

namespace Domain.Contracts;

// Music Assistant's own API, reached directly rather than through Home Assistant.
//
// Home Assistant cannot express either of these calls. `media_player.browse_media` has no podcast
// branch (it raises BrowseError for a podcast URI), `media_player.search_media` and
// `music_assistant.search` never return episodes, and `music_assistant.get_library` only lists items
// the user saved. So the episode listing a caller needs to play one specific episode exists ONLY on
// MA's native interface.
public interface IMusicAssistantClient
{
    Task<IReadOnlyList<MaMediaItem>> SearchAsync(
        string query, string mediaType, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<MaMediaItem>> GetPodcastEpisodesAsync(MaUri podcast, CancellationToken ct = default);
}

[PublicAPI]
public record MaMediaItem
{
    public required string Name { get; init; }

    // The value `music_assistant.play_media` accepts verbatim as `media_id`. Everything else about
    // an item is descriptive; this is the only field that makes it playable.
    public required string Uri { get; init; }
    public double? DurationSeconds { get; init; }
}