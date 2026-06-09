using System.Net.Sockets;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class WyomingHealthProbeService(
    VoiceSettings settings,
    IMetricsPublisher publisher,
    ILogger<WyomingHealthProbeService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = new List<(string Service, string Host, int Port)>();
        if (settings.Stt.Wyoming is { } stt)
        {
            targets.Add(("wyoming-whisper", stt.Host, stt.Port));
        }

        if (settings.Tts.Wyoming is { } tts)
        {
            targets.Add(("wyoming-piper", tts.Host, tts.Port));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (service, host, port) in targets)
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    await tcp.ConnectAsync(host, port, cts.Token);
                    await publisher.PublishAsync(new HeartbeatEvent { Service = service }, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Wyoming probe failed: {Service}@{Host}:{Port}", service, host, port);
                }
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }
}