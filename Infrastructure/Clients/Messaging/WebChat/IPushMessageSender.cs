using System.Net;

namespace Infrastructure.Clients.Messaging.WebChat;

public interface IPushMessageSender
{
    Task SendAsync(string endpoint, string p256dh, string auth, string payload, CancellationToken ct = default);
}

public sealed class WebPushSendException(string message, HttpStatusCode statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
