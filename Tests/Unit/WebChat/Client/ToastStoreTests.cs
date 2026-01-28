using Shouldly;
using WebChat.Client.State;
using WebChat.Client.State.Toast;

namespace Tests.Unit.WebChat.Client;

public sealed class ToastStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly ToastStore _store;

    public ToastStoreTests()
    {
        _store = new ToastStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void ShowError_AddsToast()
    {
        _dispatcher.Dispatch(new ShowError("Test error"));

        _store.State.Toasts.Count.ShouldBe(1);
        _store.State.Toasts[0].Message.ShouldBe("Test error");
    }

    [Fact]
    public void ShowError_WithDuplicateMessage_DoesNotAddSecondToast()
    {
        _dispatcher.Dispatch(new ShowError("Same error"));
        _dispatcher.Dispatch(new ShowError("Same error"));

        _store.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public void ShowError_ExceedsMaxToasts_RemovesOldest()
    {
        _dispatcher.Dispatch(new ShowError("Error 1"));
        _dispatcher.Dispatch(new ShowError("Error 2"));
        _dispatcher.Dispatch(new ShowError("Error 3"));
        _dispatcher.Dispatch(new ShowError("Error 4"));

        _store.State.Toasts.Count.ShouldBe(3);
        _store.State.Toasts.ShouldNotContain(t => t.Message == "Error 1");
        _store.State.Toasts.ShouldContain(t => t.Message == "Error 4");
    }

    [Fact]
    public void ShowError_WithLongMessage_TruncatesTo150Chars()
    {
        var longMessage = new string('x', 200);

        _dispatcher.Dispatch(new ShowError(longMessage));

        _store.State.Toasts[0].Message.Length.ShouldBe(153); // 150 + "..."
        _store.State.Toasts[0].Message.ShouldEndWith("...");
    }

    [Fact]
    public void ShowError_WithEmptyMessage_ShowsDefaultMessage()
    {
        _dispatcher.Dispatch(new ShowError(""));

        _store.State.Toasts[0].Message.ShouldBe("Something went wrong. Please try again.");
    }

    [Fact]
    public void DismissToast_RemovesToast()
    {
        _dispatcher.Dispatch(new ShowError("Test error"));
        var toastId = _store.State.Toasts[0].Id;

        _dispatcher.Dispatch(new DismissToast(toastId));

        _store.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public void DismissToast_WithNonExistentId_DoesNothing()
    {
        _dispatcher.Dispatch(new ShowError("Test error"));

        _dispatcher.Dispatch(new DismissToast(Guid.NewGuid()));

        _store.State.Toasts.Count.ShouldBe(1);
    }
}
