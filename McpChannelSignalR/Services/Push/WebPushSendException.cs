using System.Net;

namespace McpChannelSignalR.Services.Push;

public sealed class WebPushSendException(string message, HttpStatusCode statusCode, Exception? inner = null)
    : Exception(message, inner)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
