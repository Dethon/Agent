namespace WebChat.Client.Contracts;

public interface ISignalREventSubscriber : IDisposable
{
    bool IsSubscribed { get; }
    void Subscribe();


    void Unsubscribe();
}