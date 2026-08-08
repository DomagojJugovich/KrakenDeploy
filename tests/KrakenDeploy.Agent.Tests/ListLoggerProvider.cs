using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace KrakenDeploy.Agent.Tests;

/// <summary>
/// Minimal in-memory <see cref="ILoggerProvider"/> for asserting that a diagnostic the
/// operator depends on was actually emitted. Used where a log line IS the contract — a
/// silently clamped configuration value, for instance, is indistinguishable from an
/// honoured one without it.
/// </summary>
internal sealed class ListLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    internal IReadOnlyCollection<LogEntry> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new ListLogger(_entries, categoryName);

    public void Dispose()
    {
        // Nothing to release — the entries outlive the provider on purpose so a test can
        // assert after the host or service provider has been disposed.
    }

    internal sealed record LogEntry(LogLevel Level, string Category, string Message);

    private sealed class ListLogger(ConcurrentQueue<LogEntry> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception)));
        }
    }
}
