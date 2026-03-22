namespace Infrastructure.Clients.Push;

public interface IPushMessageSender
{
    Task SendAsync(string endpoint, string p256dh, string auth, string payload, CancellationToken ct = default);
}
