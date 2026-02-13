using System.Net.Http;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class PushServiceClientAdapter : IPushMessageSender
{
    private readonly PushServiceClient _client;
    private readonly ILogger<PushServiceClientAdapter> _logger;

    public PushServiceClientAdapter(ILogger<PushServiceClientAdapter> logger)
    {
        _logger = logger;
        var handler = new LoggingHandler(logger) { InnerHandler = new HttpClientHandler() };
        _client = new PushServiceClient(new HttpClient(handler));
    }

    public Task SendAsync(PushSubscription subscription, PushMessage message,
        VapidAuthentication authentication, CancellationToken ct = default)
        => _client.RequestPushMessageDeliveryAsync(subscription, message, authentication, ct);

    private sealed class LoggingHandler(ILogger logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            logger.LogInformation("Push request: {Method} {Uri} ContentLength={ContentLength} Headers=[{Headers}]",
                request.Method, request.RequestUri,
                request.Content?.Headers.ContentLength,
                string.Join(", ", request.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}")));

            var response = await base.SendAsync(request, ct);

            var body = response.Content != null
                ? await response.Content.ReadAsStringAsync(ct)
                : "";
            logger.LogInformation("Push response: {StatusCode} from {Uri} Body={Body}",
                (int)response.StatusCode, request.RequestUri, body);

            return response;
        }
    }
}
