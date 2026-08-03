namespace WebChat.Client.Contracts;

// Manages this browser's own push subscription. Domain.Contracts.IPushNotificationService is a
// different thing on the server side — it sends a notification to a space.
public interface IPushSubscriptionService
{
    Task<bool> RequestAndSubscribeAsync(string vapidPublicKey);
    Task ResubscribeAsync();
    Task UnsubscribeAsync();
    Task<bool> IsSubscribedAsync();
}