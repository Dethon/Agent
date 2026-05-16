using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Client.Services;

public record ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp);

public class MetricsHubService : IAsyncDisposable
{
    private readonly HubConnection? _connection;

    public MetricsHubService(Uri hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    protected MetricsHubService() { }

    public HubConnectionState ConnectionState => _connection?.State ?? HubConnectionState.Disconnected;

    public virtual IDisposable OnTokenUsage(Func<TokenUsageEvent, Task> handler) =>
        _connection!.On("OnTokenUsage", handler);

    public virtual IDisposable OnToolCall(Func<ToolCallEvent, Task> handler) =>
        _connection!.On("OnToolCall", handler);

    public virtual IDisposable OnError(Func<ErrorEvent, Task> handler) =>
        _connection!.On("OnError", handler);

    public virtual IDisposable OnScheduleExecution(Func<ScheduleExecutionEvent, Task> handler) =>
        _connection!.On("OnScheduleExecution", handler);

    public virtual IDisposable OnMemoryRecall(Func<MemoryRecallEvent, Task> handler) =>
        _connection!.On("OnMemoryRecall", handler);

    public virtual IDisposable OnMemoryExtraction(Func<MemoryExtractionEvent, Task> handler) =>
        _connection!.On("OnMemoryExtraction", handler);

    public virtual IDisposable OnMemoryDreaming(Func<MemoryDreamingEvent, Task> handler) =>
        _connection!.On("OnMemoryDreaming", handler);

    public virtual IDisposable OnContextTruncation(Func<ContextTruncationEvent, Task> handler) =>
        _connection!.On("OnContextTruncation", handler);

    public virtual IDisposable OnLatency(Func<LatencyEvent, Task> handler) =>
        _connection!.On("OnLatency", handler);

    public virtual IDisposable OnHealthUpdate(Func<ServiceHealthUpdate, Task> handler) =>
        _connection!.On("OnHealthUpdate", handler);

    public virtual void OnReconnected(Func<string?, Task> handler) =>
        _connection!.Reconnected += handler;

    public virtual void OnClosed(Func<Exception?, Task> handler) =>
        _connection!.Closed += handler;

    public virtual void OnReconnecting(Func<Exception?, Task> handler) =>
        _connection!.Reconnecting += handler;

    public virtual Task StartAsync(CancellationToken ct = default) => _connection!.StartAsync(ct);

    public virtual async ValueTask DisposeAsync() => await _connection!.DisposeAsync();
}