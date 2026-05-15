using System.Collections.Concurrent;
using JetBrains.Annotations;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

public class BrowserSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly ITimer? _pruneTimer;

    public BrowserSessionManager(
        TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? pruneInterval = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);

        if (pruneInterval is { } interval)
        {
            _pruneTimer = _timeProvider.CreateTimer(
                _ => _ = SafePruneAsync(),
                state: null,
                dueTime: interval,
                period: interval);
        }
    }

    private async Task SafePruneAsync()
    {
        try
        {
            await PruneIdleAsync();
        }
        catch
        {
            // Why: a single failure must not kill the periodic timer
        }
    }

    private void Touch(string sessionId, BrowserSession session)
    {
        _sessions[sessionId] = session with { LastAccessedAt = _timeProvider.GetUtcNow() };
    }

    public async Task<BrowserSession> GetOrCreateAsync(
        string sessionId,
        IBrowserContext context,
        CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing) && !existing.Page.IsClosed)
        {
            Touch(sessionId, existing);
            return existing;
        }

        await _createLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(sessionId, out existing) && !existing.Page.IsClosed)
            {
                Touch(sessionId, existing);
                return existing;
            }

            var page = await context.NewPageAsync();

            // Why: Playwright blocks the page until dialogs are handled
            page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

            var now = _timeProvider.GetUtcNow();
            var session = new BrowserSession(
                SessionId: sessionId,
                Page: page,
                CurrentUrl: "about:blank",
                CreatedAt: now,
                LastAccessedAt: now
            );

            _sessions[sessionId] = session;
            return session;
        }
        finally
        {
            _createLock.Release();
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
                LastAccessedAt = _timeProvider.GetUtcNow()
            };
        }
    }

    public async Task CloseAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session) && !session.Page.IsClosed)
        {
            await session.Page.CloseAsync();
        }
    }

    public async Task PruneIdleAsync()
    {
        var cutoff = _timeProvider.GetUtcNow() - _idleTimeout;
        var idleIds = _sessions
            .Where(kv => kv.Value.LastAccessedAt < cutoff)
            .Select(kv => kv.Key);

        await Task.WhenAll(idleIds.Select(CloseAsync));
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
        if (_pruneTimer is not null)
        {
            await _pruneTimer.DisposeAsync();
        }
        await CloseAllAsync();
        _createLock.Dispose();
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