using Microsoft.Extensions.Logging;
using Shouldly;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

public class RecordingLogger : ILogger
{
    private readonly List<LogEntry> _entries = [];
    private readonly SemaphoreSlim _written = new(0);

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    // Fault logging happens on a continuation, so a test cannot assume the entry is there
    // the moment the awaited work returns.
    public async Task<LogEntry> WaitForEntryAsync()
    {
        var written = await _written.WaitAsync(TimeSpan.FromSeconds(5));
        written.ShouldBeTrue("no log entry was written within the timeout");
        return Entries[0];
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }

        _written.Release();
    }
}

public sealed class RecordingLogger<T> : RecordingLogger, ILogger<T>;