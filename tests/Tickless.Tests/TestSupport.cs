using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Tickless.Tests;

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>Captures log output so tests can assert on what the scheduler reported.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
}

internal static class Wait
{
    /// <summary>
    /// Polls until <paramref name="condition"/> holds. Used where the scheduler finishes work on a
    /// thread-pool continuation that the test has no handle on.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// Fires every timer on the creating thread, before <see cref="TimeProvider.CreateTimer"/> returns.
/// A job scheduled with it runs its execution synchronously inside <c>ScheduleAsync</c>, which
/// exposes anything that happens only after the execution has started.
/// </summary>
internal sealed class ImmediateTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Start;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        callback(state);
        return new SpentTimer();
    }

    private sealed class SpentTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
