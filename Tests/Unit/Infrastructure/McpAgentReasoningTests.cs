using System.Net;
using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentReasoningTests
{
    [Fact]
    public void ParseEffort_NullOrBlank_ReturnsNull()
    {
        McpAgent.ParseEffort(null).ShouldBeNull();
        McpAgent.ParseEffort("").ShouldBeNull();
        McpAgent.ParseEffort("   ").ShouldBeNull();
    }

    [Theory]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("LOW", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    [InlineData("none", ReasoningEffort.None)]
    public void ParseEffort_Known_MapsToFrameworkEnum(string input, ReasoningEffort expected)
    {
        McpAgent.ParseEffort(input).ShouldBe(expected);
    }

    [Fact]
    public void ParseEffort_Unknown_Throws()
    {
        Should.Throw<ArgumentException>(() => McpAgent.ParseEffort("ridiculous"));
    }

    [Fact]
    public async Task McpAgent_WithEffortConfigured_EmitsReasoningEffortInWireBody()
    {
        // End-to-end: real OpenRouterChatClient + capturing inner handler + McpAgent set up
        // with reasoningEffort = "medium". Drives a turn through the agent and asserts the
        // outgoing OpenRouter JSON body contains reasoning_effort.
        var captured = new TaskCompletionSource<string>();
        var captureHandler = new CapturingHandler(captured);

        using var openRouter = OpenRouterChatClient.CreateForTesting(
            "https://openrouter.ai/api/v1/", "test-key", "z-ai/glm-5.1", captureHandler);

        var stateStore = new Mock<IThreadStateStore>().Object;
        await using var agent = new McpAgent(
            endpoints: [],
            chatClient: openRouter,
            name: "test-agent",
            description: "",
            stateStore: stateStore,
            userId: "u",
            reasoningEffort: "medium");

        try
        {
            await foreach (var _ in agent.RunStreamingAsync("hi"))
            {
            }
        }
        catch
        {
            // We only care about the captured outbound body.
        }

        var body = await captured.Task;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("reasoning_effort").GetString().ShouldBe("medium");
    }

    [Fact]
    public async Task McpAgent_WithoutEffort_DoesNotSetReasoning()
    {
        var captured = new TaskCompletionSource<string>();
        var captureHandler = new CapturingHandler(captured);

        using var openRouter = OpenRouterChatClient.CreateForTesting(
            "https://openrouter.ai/api/v1/", "test-key", "z-ai/glm-5.1", captureHandler);

        var stateStore = new Mock<IThreadStateStore>().Object;
        await using var agent = new McpAgent(
            endpoints: [],
            chatClient: openRouter,
            name: "test-agent",
            description: "",
            stateStore: stateStore,
            userId: "u");

        try
        {
            await foreach (var _ in agent.RunStreamingAsync("hi"))
            {
            }
        }
        catch
        {
            // ignore
        }

        var body = await captured.Task;
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("reasoning_effort", out _).ShouldBeFalse();
    }

    private sealed class CapturingHandler(TaskCompletionSource<string> captured) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            captured.TrySetResult(body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
