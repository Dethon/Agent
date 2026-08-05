namespace WebChat.Client.State.Toast;

public record ShowError(string Message) : IAction;

public record DismissToast(Guid Id) : IAction;

public sealed class ToastStore : IDisposable
{
    private const int MaxToasts = 3;
    private const int MaxMessageLength = 150;
    private const string FallbackMessage = "Something went wrong. Please try again.";

    private readonly Store<ToastState> _store;

    public ToastStore(Dispatcher dispatcher)
    {
        _store = new Store<ToastState>(ToastState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public ToastState State => _store.State;
    public IObservable<ToastState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static ToastState Reduce(ToastState state, IAction action) => action switch
    {
        ShowError a => Show(state, a),
        DismissToast a => new ToastState(state.Toasts.RemoveAll(t => t.Id == a.Id)),
        _ => state
    };

    private static ToastState Show(ToastState state, ShowError action)
    {
        var message = TruncateMessage(action.Message);

        if (state.Toasts.Any(t => t.Message == message))
        {
            return state;
        }

        var toasts = state.Toasts.Add(new ToastItem(Guid.NewGuid(), message, DateTime.UtcNow));

        return new ToastState(toasts.Count > MaxToasts ? toasts.RemoveAt(0) : toasts);
    }

    private static string TruncateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return FallbackMessage;
        }

        return message.Length <= MaxMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaxMessageLength), "...");
    }
}