using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterChatClientSharedHandlerTests
{
    [Fact]
    public async Task Dispose_DoesNotDisposeSharedHandler()
    {
        var client = new OpenRouterChatClient("https://example.invalid/v1/", "key", "model");
        client.Dispose();

        using var invoker = new HttpMessageInvoker(OpenRouterChatClient.SharedHandler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9/");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        // A disposed handler throws ObjectDisposedException synchronously before any I/O;
        // a live handler attempts the connect and is cancelled by the timeout.
        var ex = await Record.ExceptionAsync(() => invoker.SendAsync(request, cts.Token));
        ex.ShouldNotBeNull();
        ex.ShouldNotBeOfType<ObjectDisposedException>();
    }
}