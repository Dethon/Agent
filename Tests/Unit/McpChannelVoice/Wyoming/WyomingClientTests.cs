using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingClientTests
{
    [Fact]
    public async Task ConnectAsync_RoundTripsAnEvent()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            var reader = new WyomingReader(stream);
            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "describe")
                {
                    var writer = new WyomingWriter(stream);
                    await writer.WriteAsync(
                        WyomingEvent.Header("info", new JsonObject { ["foo"] = "bar" }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        await using var client = new WyomingClient();
        await client.ConnectAsync("127.0.0.1", port, CancellationToken.None);
        await client.WriteAsync(
            WyomingEvent.Header("describe", new JsonObject()),
            CancellationToken.None);

        WyomingEvent? received = null;
        await foreach (var evt in client.ReadAllAsync(CancellationToken.None))
        {
            received = evt;
            break;
        }

        await serverTask;
        listener.Stop();

        received.ShouldNotBeNull();
        received!.Type.ShouldBe("info");
        received.Data["foo"]!.GetValue<string>().ShouldBe("bar");
    }
}