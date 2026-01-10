using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.Playwright;

namespace Infrastructure.Clients;

public class BrowserSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
    private readonly Lock _lock = new();

    public Task<BrowserSession> GetOrCreateAsync(
        string sessionId,
        IBrowserContext context,
        CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            return Task.FromResult(existing with { LastAccessedAt = DateTimeOffset.UtcNow });
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(sessionId, out existing))
            {
                return Task.FromResult(existing with { LastAccessedAt = DateTimeOffset.UtcNow });
            }

            var page = context.NewPageAsync().GetAwaiter().GetResult();
            var session = new BrowserSession(
                SessionId: sessionId,
                Page: page,
                CurrentUrl: "about:blank",
                CreatedAt: DateTimeOffset.UtcNow,
                LastAccessedAt: DateTimeOffset.UtcNow
            );

            _sessions[sessionId] = session;
            return Task.FromResult(session);
        }
    }

    public BrowserSession? Get(string sessionId)
    {
        return _sessions.GetValueOrDefault(sessionId);
    }

    public void UpdateCurrentUrl(string sessionId, string url)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            _sessions[sessionId] = session with
            {
                CurrentUrl = url,
                LastAccessedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public async Task CloseAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            if (!session.Page.IsClosed)
            {
                await session.Page.CloseAsync();
            }
        }
    }

    private async Task CloseAllAsync()
    {
        var sessions = _sessions.Values.ToList();
        _sessions.Clear();

        foreach (var session in sessions.Where(session => !session.Page.IsClosed))
        {
            await session.Page.CloseAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync();
        GC.SuppressFinalize(this);
    }
}

[UsedImplicitly]
public record BrowserSession(
    string SessionId,
    IPage Page,
    string CurrentUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt);