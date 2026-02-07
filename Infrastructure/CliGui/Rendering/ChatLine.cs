namespace Infrastructure.CliGui.Rendering;

public sealed record ChatLine(
    string Text,
    ChatLineType Type,
    string? GroupId = null,
    bool IsCollapsible = false);