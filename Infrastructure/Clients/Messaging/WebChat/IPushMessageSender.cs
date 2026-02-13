using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Infrastructure.Clients.Messaging.WebChat;

public interface IPushMessageSender
{
    Task SendAsync(PushSubscription subscription, PushMessage message, VapidAuthentication authentication,
        CancellationToken ct = default);
}
