using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Client.Services;

public record ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp);

public sealed class MetricsHubService : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public MetricsHubService(Uri hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    public HubConnectionState ConnectionState => _connection.State;

    public IDisposable OnTokenUsage(Func<TokenUsageEvent, Task> handler) =>
        _connection.On("OnTokenUsage", handler);

    public IDisposable OnToolCall(Func<ToolCallEvent, Task> handler) =>
        _connection.On("OnToolCall", handler);

    public IDisposable OnError(Func<ErrorEvent, Task> handler) =>
        _connection.On("OnError", handler);

    public IDisposable OnScheduleExecution(Func<ScheduleExecutionEvent, Task> handler) =>
        _connection.On("OnScheduleExecution", handler);

    public IDisposable OnHealthUpdate(Func<ServiceHealthUpdate, Task> handler) =>
        _connection.On("OnHealthUpdate", handler);

    public event Func<string?, Task>? Reconnected
    {
        add => _connection.Reconnected += value;
        remove => _connection.Reconnected -= value;
    }

    public event Func<Exception?, Task>? Closed
    {
        add => _connection.Closed += value;
        remove => _connection.Closed -= value;
    }

    public event Func<Exception?, Task>? Reconnecting
    {
        add => _connection.Reconnecting += value;
        remove => _connection.Reconnecting -= value;
    }

    public Task StartAsync(CancellationToken ct = default) => _connection.StartAsync(ct);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
