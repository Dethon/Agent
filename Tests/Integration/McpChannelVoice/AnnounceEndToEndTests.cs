using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class AnnounceEndToEndTests
{
    [Fact]
    public async Task PostAnnounce_PushesAudioToConnectedSatellite()
    {
        var settings = new VoiceSettings
        {
            WyomingServer = new() { Host = "127.0.0.1", Port = 0 },
            Announce = new() { Enabled = true, Token = "secret", QueueMaxDepth = 4 },
            Satellites = new()
            {
                ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
            }
        };

        var port = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(settings.Announce);
        builder.Services.AddSingleton(settings.WyomingServer);
        builder.Services.AddSingleton(new SatelliteRegistry(settings.Satellites));
        builder.Services.AddSingleton<SatelliteSessionRegistry>();
        builder.Services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => FakeTtsAudio());
        builder.Services.AddSingleton(tts.Object);
        builder.Services.AddSingleton<TranscriptDispatcher>(_ => null!);

        var stt = new Mock<ISpeechToText>();
        builder.Services.AddSingleton(stt.Object);

        builder.Services.AddSingleton<AnnouncementService>();
        builder.Services.AddHostedService<WyomingServer>();

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync();

        var wyomingServer = app.Services.GetServices<IHostedService>().OfType<WyomingServer>().Single();

        using var satellite = new TcpClient();
        await satellite.ConnectAsync(IPAddress.Loopback, wyomingServer.BoundPort);
        await using var satStream = satellite.GetStream();
        var satWriter = new WyomingWriter(satStream);
        var satReader = new WyomingReader(satStream);

        await satWriter.WriteAsync(WyomingEvent.Header("info",
            new JsonObject { ["satellite"] = new JsonObject { ["name"] = "kitchen-01" } }),
            CancellationToken.None);

        await Task.Delay(150); // let session register

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-Announce-Token", "secret");

        var response = await http.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "hello",
                Priority = AnnouncePriority.Normal
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Expect a Wyoming audio-chunk back on the satellite stream within 1 s.
        var sawAudio = false;
        var ctsRead = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var evt in satReader.ReadAllAsync(ctsRead.Token))
            {
                if (evt.Type == "audio-chunk")
                {
                    sawAudio = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* ignore */ }

        sawAudio.ShouldBeTrue();

        await app.StopAsync();
    }

    private static async IAsyncEnumerable<AudioChunk> FakeTtsAudio()
    {
        yield return new AudioChunk { Data = new byte[32], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}