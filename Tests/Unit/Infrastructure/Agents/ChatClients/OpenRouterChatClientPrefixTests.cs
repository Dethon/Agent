using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
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
    public async Task GetStreamingResponseAsync_WithSenderLocationAndSatellite_RendersViaSatellite()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetLocation("the office");
        msg.SetSatelliteId("kitchen-01");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household (in the office via kitchen-01):");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithSenderAndSatelliteNoLocation_RendersViaSatellite()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        msg.SetSatelliteId("kitchen-01");

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("Message from household (via kitchen-01):");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithSatelliteButNoSender_IgnoresSatellite()
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSatelliteId("kitchen-01");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero));

        await _sut.GetStreamingResponseAsync([msg]).ToListAsync();

        FirstText().ShouldStartWith("[Current time: ");
        FirstText().ShouldNotContain("Message from");
        FirstText().ShouldNotContain("via");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task GetStreamingResponseAsync_WithSenderAndBlankOrNoLocation_OmitsRoom(string? location)
    {
        var msg = new ChatMessage(ChatRole.User, "lights on");
        msg.SetSenderId("household");
        if (location is not null)
        {
            msg.AdditionalProperties!["Location"] = location;
        }

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

    [Fact]
    public async Task GetStreamingResponseAsync_RendersTimestampInLocalZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "p2", "p2");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        clock.SetLocalTimeZone(zone);
        using var sut = new OpenRouterChatClient(_innerClient.Object, "test-model", timeProvider: clock);

        var msg = new ChatMessage(ChatRole.User, "hi");
        msg.SetSenderId("u");
        msg.SetTimestamp(new DateTimeOffset(2026, 6, 4, 18, 22, 1, TimeSpan.Zero)); // 18:22:01 UTC

        await sut.GetStreamingResponseAsync([msg]).ToListAsync();

        var first = _captured[0].Contents.OfType<TextContent>().First().Text;
        first.ShouldStartWith("[Current time: 2026-06-04 20:22:01 +02:00]");
    }
}