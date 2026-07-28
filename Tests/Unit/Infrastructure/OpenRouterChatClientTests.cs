using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// Everything below the constructor is otherwise invisible to tests: MultiAgentFactoryTests
// replace the whole client, OpenRouterHttpHelpersTests call the static helper directly, and
// the metrics tests use the inner-IChatClient ctor that never builds the HTTP pipeline. This
// is the only test that proves the ctor's providerRouting/sessionId survive the
// CreateHttpClient -> ReasoningHandler hop onto an actual outgoing request.
public sealed class OpenRouterChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_CtorRoutingAndSession_ReachTheOutgoingRequestBody()
    {
        var handler = new CapturingHandler();
        using var client = new OpenRouterChatClient(
            "http://localhost/api/v1",
            "test-key",
            "test-model",
            sessionId: "session-1",
            providerRouting: new ProviderRouting { Sort = ProviderSort.Latency },
            transportHandler: handler);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var body = JsonNode.Parse(handler.CapturedBody!)!.AsObject();
        body["provider"]!["sort"]!.GetValue<string>().ShouldBe("latency");
        body["session_id"]!.GetValue<string>().ShouldBe("session-1");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string Sse =
            """
            data: {"id":"1","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Sse, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}