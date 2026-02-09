namespace WebChat.Client.State.Space;

public static class SpaceReducers
{
    public static SpaceState Reduce(SpaceState state, IAction action) => action switch
    {
        SelectSpace a => state with { CurrentSlug = a.Slug },
        SpaceValidated a => state with { CurrentSlug = a.Slug, AccentColor = a.AccentColor },
        InvalidSpace => SpaceState.Initial,
        _ => state
    };
}
