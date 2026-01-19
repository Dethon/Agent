namespace WebChat.Client.State;

/// <summary>
/// Dispatches actions to registered handlers.
/// Components inject this to dispatch actions without knowing specific stores.
/// </summary>
public interface IDispatcher
{
    void Dispatch<TAction>(TAction action) where TAction : IAction;
}
