using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterChatClientPrefixTests : IDisposable
{
    private readonly Mock<IChatClient> _innerClient = new();
    private readonly OpenRouterChatClient _sut;
    private IReadOnlyList<ChatMessage> _captured = [];

    public OpenRouterChatClientPrefixTests()
    {
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (messages, _, _) => _captured = messages.ToList())
            .Returns(Array.Empty<ChatResponseUpdate>().ToAsyncEnumerable());

        _sut = new OpenRouterChatClient(_innerClient.Object, "test-model");
    }

    public void Dispose() => _sut.Dispose();

    private string FirstText() =>
        _captured[0].Contents.OfType<TextContent>().First().Text;

    [Fact]
    public async Task GetStreamingResponseAsync_WithSenderAndLocation_PrefixesRoom()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetLocation("the office");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household (in the office):");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithSenderNoLocation_OmitsRoom()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household:");
        FirstText().ShouldNotContain("(in");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithWhitespaceLocation_OmitsRoom()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.AdditionalProperties!["Location"] = "   ";

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household:");
        FirstText().ShouldNotContain("(in");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithLocationButNoSender_IgnoresLocation()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetLocation("the office");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("[Current time: ");
        FirstText().ShouldNotContain("Message from");
        FirstText().ShouldNotContain("(in");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithTimestampOnly_RendersTimeWithoutSender()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("[Current time: ");
        FirstText().ShouldContain("]:\n");
        FirstText().ShouldNotContain("Message from");
    }
}