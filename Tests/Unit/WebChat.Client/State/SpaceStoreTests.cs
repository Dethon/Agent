using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Space;

namespace Tests.Unit.WebChat.Client.State;

public class SpaceStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SpaceStore _store;

    public SpaceStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new SpaceStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void Initial_HasDefaultSlugAndColor()
    {
        _store.State.CurrentSlug.ShouldBe("default");
        _store.State.AccentColor.ShouldBe(SpaceConfig.DefaultAccentColor);
    }

    [Fact]
    public void SpaceValidated_UpdatesSlugAndAccentColor()
    {
        _dispatcher.Dispatch(new SpaceValidated("secret-room", "Secret Room", "#6366f1"));

        _store.State.CurrentSlug.ShouldBe("secret-room");
        _store.State.AccentColor.ShouldBe("#6366f1");
    }

    [Fact]
    public void InvalidSpace_ResetsToDefault()
    {
        _dispatcher.Dispatch(new SpaceValidated("secret-room", "Secret Room", "#6366f1"));
        _dispatcher.Dispatch(new InvalidSpace());

        _store.State.CurrentSlug.ShouldBe("default");
        _store.State.AccentColor.ShouldBe(SpaceConfig.DefaultAccentColor);
    }

    [Fact]
    public void SelectSpace_UpdatesCurrentSlug()
    {
        _dispatcher.Dispatch(new SelectSpace("my-space"));

        _store.State.CurrentSlug.ShouldBe("my-space");
    }
}
