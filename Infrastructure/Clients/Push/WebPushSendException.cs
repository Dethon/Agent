using System.Net;

namespace Infrastructure.Clients.Push;

public sealed class WebPushSendException(string message, HttpStatusCode statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
