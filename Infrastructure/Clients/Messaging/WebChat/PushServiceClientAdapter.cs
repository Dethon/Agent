using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class PushServiceClientAdapter(PushServiceClient client) : IPushMessageSender
{
    public Task SendAsync(PushSubscription subscription, PushMessage message, VapidAuthentication authentication,
        CancellationToken ct = default)
        => client.RequestPushMessageDeliveryAsync(subscription, message, authentication, ct);
}
