namespace WebChat.Client.Contracts;

public interface ISignalREventSubscriber : IDisposable
{
    void Subscribe();


    void Unsubscribe();


    bool IsSubscribed { get; }
}