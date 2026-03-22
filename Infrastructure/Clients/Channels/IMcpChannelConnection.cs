namespace Infrastructure.Clients.Channels;

public interface IMcpChannelConnection
{
    string ChannelId { get; }
    Task ConnectAsync(string endpoint, CancellationToken ct);
    Task<bool> IsHealthyAsync(CancellationToken ct);
    Task ReconnectAsync(string endpoint, CancellationToken ct);
}
