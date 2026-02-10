using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Space;

public sealed record SpaceState
{
    public string CurrentSlug { get; init; } = "default";
    public string SpaceName { get; init; } = "Main";
    public string AccentColor { get; init; } = SpaceConfig.DefaultAccentColor;

    public static SpaceState Initial => new();
}
