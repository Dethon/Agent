namespace WebChat.Client.Contracts;

public interface ISignalREventSubscriber : IDisposable
{
    /// <summary>
    /// Registers event handlers for all SignalR hub notifications.
    /// Idempotent - calling multiple times has no effect after first subscription.
    /// </summary>
    void Subscribe();

    /// <summary>
    /// Disposes all registered event handlers.
    /// After calling, Subscribe() can be called again to re-register.
    /// </summary>
    void Unsubscribe();

    /// <summary>
    /// Indicates whether event handlers are currently registered.
    /// </summary>
    bool IsSubscribed { get; }
}
