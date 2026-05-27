using System.Threading.Channels;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionTests
{
    [Fact]
    public async Task InboundAudio_CompletesWhenSessionClosed()
    {
        var session = new SatelliteSession(
            satelliteId: "kitchen-01",
            config: new SatelliteConfig { Identity = "household", Room = "Kitchen" });

        await session.PublishAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        session.CompleteInboundAudio();

        var bytes = new List<byte>();
        await foreach (var chunk in session.ReadInboundAudioAsync(CancellationToken.None))
        {
            bytes.AddRange(chunk.ToArray());
        }
        bytes.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void ConversationId_EqualsSatelliteId()
    {
        var session = new SatelliteSession(
            satelliteId: "bedroom-01",
            config: new SatelliteConfig { Identity = "francisco", Room = "Bedroom" });

        session.ConversationId.ShouldBe("bedroom-01");
    }
}