namespace WebChat.Client.State.Space;

public record SelectSpace(string Slug) : IAction;
public record SpaceValidated(string Slug, string Name, string AccentColor) : IAction;
public record InvalidSpace : IAction;
