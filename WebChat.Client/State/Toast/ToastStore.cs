namespace WebChat.Client.State.Toast;

public sealed class ToastStore : IDisposable
{
    private const int MaxToasts = 3;
    private const int MaxMessageLength = 150;

    private readonly Store<ToastState> _store;

    public ToastStore(Dispatcher dispatcher)
    {
        _store = new Store<ToastState>(ToastState.Initial);

        dispatcher.RegisterHandler<ShowError>(action =>
            _store.Dispatch(action, Reduce));
        dispatcher.RegisterHandler<DismissToast>(action =>
            _store.Dispatch(action, Reduce));
    }

    public ToastState State => _store.State;
    public IObservable<ToastState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static ToastState Reduce(ToastState state, ShowError action)
    {
        var message = TruncateMessage(action.Message);

        // Deduplicate: don't add if same message already visible
        if (state.Toasts.Any(t => t.Message == message))
        {
            return state;
        }

        var toast = new ToastItem(Guid.NewGuid(), message, DateTime.UtcNow);
        var toasts = state.Toasts.Add(toast);

        // Enforce max limit by removing oldest
        if (toasts.Count > MaxToasts)
        {
            toasts = toasts.RemoveAt(0);
        }

        return new ToastState(Toasts: toasts);
    }

    private static ToastState Reduce(ToastState state, DismissToast action)
    {
        var toasts = state.Toasts.RemoveAll(t => t.Id == action.Id);
        return new ToastState(Toasts: toasts);
    }

    private static string TruncateMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Something went wrong. Please try again.";
        }

        return message.Length <= MaxMessageLength
            ? message
            : string.Concat(message.AsSpan(0, MaxMessageLength), "...");
    }
}