using Microsoft.Extensions.Logging;

namespace Tests.Unit;

// Collects formatted log messages so a test can assert on what an operator would see.
// The three call sites differ only in which levels they keep, hence the filter.
internal sealed class CapturingLoggerProvider(Func<LogLevel, bool> keep) : ILoggerProvider
{
    public CapturingLoggerProvider(LogLevel minimum) : this(level => level >= minimum)
    {
    }

    public List<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages, keep);

    public void Dispose()
    {
    }

    public static CapturingLoggerProvider ForLevel(LogLevel level) => new(l => l == level);

    private sealed class CapturingLogger(List<string> messages, Func<LogLevel, bool> keep) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => keep(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (keep(logLevel))
            {
                messages.Add(formatter(state, exception));
            }
        }
    }
}