using System.Collections.Immutable;

namespace WebChat.Client.State.Toast;

public sealed record ToastItem(Guid Id, string Message, DateTime CreatedAt);

public sealed record ToastState(ImmutableList<ToastItem> Toasts)
{
    public static ToastState Initial => new(ImmutableList<ToastItem>.Empty);
}
