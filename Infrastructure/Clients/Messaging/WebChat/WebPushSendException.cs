using System.Net;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class WebPushSendException(string message, HttpStatusCode statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
