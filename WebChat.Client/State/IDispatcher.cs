namespace WebChat.Client.State;

public interface IDispatcher
{
    void Dispatch<TAction>(TAction action) where TAction : IAction;
}