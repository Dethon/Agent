namespace WebChat.Client.State.Space;

public sealed record SpaceState
{
    public string CurrentSlug { get; init; } = "default";
    public string SpaceName { get; init; } = "Main";
    public string AccentColor { get; init; } = "#e94560";

    public static SpaceState Initial => new();
}
