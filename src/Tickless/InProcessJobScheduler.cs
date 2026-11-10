using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Tickless;

/// <summary>
/// In-process implementation of <see cref="IJobScheduler"/> backed by
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// Event-driven within a single process: jobs fire at their exact scheduled instant without polling.
/// On startup, recovers pending jobs from the database via each registered
/// <see cref="IJobHandler"/>'s <see cref="IJobHandler.RecoverPendingJobsAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Single-instance only. Two replicas of the host will both fire every job. For horizontally
/// scaled deployments, pair this scheduler with a leader-election mechanism or use a queue-backed
/// alternative (Hangfire, Quartz, or Azure Service Bus scheduled messages).
/// </para>
/// <para>
/// On shutdown, <see cref="StopAsync"/> cancels every timer, signals the cancellation token passed
/// to running handlers, and waits for them to finish for as long as the host allows
/// (<c>HostOptions.ShutdownTimeout</c>).
/// </para>
/// </remarks>
public sealed class InProcessJobScheduler : IJobScheduler, IHostedService
{
    private readonly ConcurrentDictionary<string, ScheduledEntry> _scheduledJobs = new();
    // Every execution task that has not finished yet: armed timers and running handlers.
    private readonly ConcurrentDictionary<Task, byte> _executions = new();
    // Cancelled by StopAsync. Every timer token is linked to it, and handlers receive its token.
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dictionary<string, IJobHandler> _handlers;
    private readonly ILogger<InProcessJobScheduler> _logger;
    private readonly TimeProvider _time;

    /// <summary>
    /// Creates a new in-process scheduler. Handlers are typically registered via DI; the constructor
    /// builds the type-to-handler lookup once.
    /// </summary>
    public InProcessJobScheduler(
        IEnumerable<IJobHandler> handlers,
        ILogger<InProcessJobScheduler> logger)
        : this(handlers, logger, TimeProvider.System)
    {
    }

    // Tests substitute a FakeTimeProvider here. DI only sees public constructors, so the public
    // surface stays the same as 1.0.
    internal InProcessJobScheduler(
        IEnumerable<IJobHandler> handlers,
        ILogger<InProcessJobScheduler> logger,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _time = timeProvider;
        _handlers = handlers.ToDictionary(h => h.JobType);
    }

    /// <inheritdoc />
    public Task ScheduleAsync(string jobId, DateTime executeAtUtc, string jobType, string? payload = null)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(jobType);

        // Checked before the map is touched, so a bad call never cancels an existing job.
        if (!_handlers.ContainsKey(jobType))
        {
            var registered = _handlers.Count == 0
                ? "none"
                : string.Join(", ", _handlers.Keys.Order(StringComparer.Ordinal).Select(t => $"'{t}'"));
            throw new ArgumentException(
                $"No handler is registered for job type '{jobType}'. Registered job types: {registered}.",
                nameof(jobType));
        }

        if (_stopping.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Tickless is stopping; job '{JobId}' (type: {JobType}) was not scheduled.", jobId, jobType);
            return Task.CompletedTask;
        }

        var executeAt = NormalizeToUtc(executeAtUtc);
        var entry = new ScheduledEntry(executeAt, jobType, _stopping.Token);
        Install(jobId, entry);

        var delay = executeAt - _time.GetUtcNow().UtcDateTime;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _logger.LogInformation(
            "Scheduled job '{JobId}' (type: {JobType}) to execute at {ExecuteAt:u} (in {Delay}).",
            jobId, jobType, executeAt, delay);

        Track(ExecuteAfterDelayAsync(jobId, entry, delay, payload));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelAsync(string jobId)
    {
        if (CancelInternal(jobId))
        {
            _logger.LogInformation("Cancelled job '{JobId}'.", jobId);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ScheduledJobView> GetScheduled()
    {
        return _scheduledJobs
            .Select(kvp => new ScheduledJobView(kvp.Key, kvp.Value.ExecuteAtUtc, kvp.Value.JobType))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Execution tasks that have not finished: armed timers plus running handlers. Exposed for tests.
    /// </summary>
    internal int InFlightExecutions => _executions.Count;

    /// <summary>
    /// Test seam: called with the job id after a job's timer has fired and before its handler is
    /// called, so a test can cancel or reschedule the job inside that window.
    /// </summary>
    internal Action<string>? TimerFired { get; set; }

    /// <summary>
    /// Returns the same instant with <see cref="DateTimeKind.Utc"/>. A <see cref="DateTimeKind.Local"/>
    /// value is converted. An <see cref="DateTimeKind.Unspecified"/> value is taken to be UTC already,
    /// because that is how EF Core and most ADO.NET providers read a UTC column back.
    /// </summary>
    internal static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>
    /// Splits a delay larger than <see cref="int.MaxValue"/> milliseconds (~24.9 days) into chunks
    /// that <see cref="Task.Delay(TimeSpan, CancellationToken)"/> can wait on.
    /// </summary>
    internal static IEnumerable<TimeSpan> ChunkDelay(TimeSpan total)
    {
        if (total < TimeSpan.Zero) total = TimeSpan.Zero;

        if (total == TimeSpan.Zero)
        {
            yield return TimeSpan.Zero;
            yield break;
        }

        var max = TimeSpan.FromMilliseconds(int.MaxValue);
        while (total > max)
        {
            yield return max;
            total -= max;
        }
        if (total > TimeSpan.Zero)
        {
            yield return total;
        }
    }

    // Puts the entry in the map, replacing any existing entry for the same id. Concurrent calls for
    // one id cannot both win: TryUpdate only succeeds against the exact entry that was read, so
    // every replaced entry is seen by exactly one caller, which cancels its timer.
    private void Install(string jobId, ScheduledEntry entry)
    {
        while (true)
        {
            if (_scheduledJobs.TryGetValue(jobId, out var existing))
            {
                if (_scheduledJobs.TryUpdate(jobId, entry, existing))
                {
                    existing.Cancel();
                    return;
                }
            }
            else if (_scheduledJobs.TryAdd(jobId, entry))
            {
                return;
            }
        }
    }

    private void Track(Task execution)
    {
        _executions.TryAdd(execution, 0);
        _ = execution.ContinueWith(
            static (finished, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(finished, out _),
            _executions,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Whoever removes an entry from the map owns cancelling and disposing it.
    private bool CancelInternal(string jobId)
    {
        if (_scheduledJobs.TryRemove(jobId, out var entry))
        {
            entry.Cancel();
            return true;
        }
        return false;
    }

    private async Task ExecuteAfterDelayAsync(string jobId, ScheduledEntry entry, TimeSpan delay, string? payload)
    {
        var token = entry.Token;
        try
        {
            try
            {
                if (delay == TimeSpan.Zero)
                {
                    // A past-due job must not run on the caller's thread. ScheduleAsync would not
                    // return until the handler's first await, and StartAsync would recover overdue
                    // jobs one handler run at a time.
                    await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                }

                foreach (var chunk in ChunkDelay(delay))
                {
                    await Task.Delay(chunk, _time, token).ConfigureAwait(false);
                }

                TimerFired?.Invoke(jobId);

                // A cancel or reschedule can land after the delay completes and before the handler
                // is called. Checking here keeps it from running the job it was meant to stop.
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The timer was cancelled before the handler started: the job was replaced or
                // cancelled, or the scheduler is stopping.
                return;
            }

            _logger.LogInformation("Executing job '{JobId}' (type: {JobType}).", jobId, entry.JobType);
            try
            {
                // The handler gets the scheduler-wide stopping token, not this entry's timer token:
                // rescheduling or cancelling a job affects its timer, never a handler already running.
                await _handlers[entry.JobType].ExecuteAsync(payload, _stopping.Token).ConfigureAwait(false);
                _logger.LogInformation("Job '{JobId}' completed successfully.", jobId);
            }
            catch (OperationCanceledException ex) when (_stopping.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Job '{JobId}' (type: {JobType}) was interrupted by shutdown. It runs again on the next start if recovery still reports it.",
                    jobId, entry.JobType);
            }
            catch (Exception ex)
            {
                // Includes an OperationCanceledException the handler raised itself, such as an
                // HttpClient timeout. Nothing in the scheduler asked it to stop, so it is a failure.
                _logger.LogError(ex, "Job '{JobId}' (type: {JobType}) failed.", jobId, entry.JobType);
            }
        }
        finally
        {
            // Remove only the entry this execution armed. If the job was rescheduled while the
            // handler ran, the map now holds a newer entry for the same id and it must survive.
            if (_scheduledJobs.TryRemove(KeyValuePair.Create(jobId, entry)))
            {
                entry.Release();
            }
        }
    }

    /// <summary>
    /// Recovers pending work by calling each handler's
    /// <see cref="IJobHandler.RecoverPendingJobsAsync(CancellationToken)"/> and scheduling what it returns.
    /// </summary>
    /// <remarks>
    /// The host waits for this to finish before it starts serving, so recovery queries should be
    /// cheap, indexed reads. A handler whose recovery throws is logged and skipped; the others
    /// still recover.
    /// </remarks>
    /// <param name="cancellationToken">Signalled when the host abandons startup.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tickless starting - recovering pending jobs from domain state.");

        foreach (var handler in _handlers.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var pending = (await handler.RecoverPendingJobsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                foreach (var job in pending)
                {
                    await ScheduleAsync(job.JobId, job.ExecuteAtUtc, handler.JobType, job.Payload).ConfigureAwait(false);
                }

                if (pending.Count > 0)
                {
                    _logger.LogInformation(
                        "Recovered {Count} pending job(s) for handler '{JobType}'.",
                        pending.Count, handler.JobType);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover jobs for handler '{JobType}'.", handler.JobType);
            }
        }

        _logger.LogInformation("Tickless started with {Count} handler(s) registered.", _handlers.Count);
    }

    /// <summary>
    /// Stops the scheduler: cancels every timer, signals the cancellation token passed to running
    /// handlers, and waits for those handlers to finish.
    /// </summary>
    /// <remarks>
    /// The wait ends when every handler has finished or <paramref name="cancellationToken"/> is
    /// cancelled, which the host does when <c>HostOptions.ShutdownTimeout</c> elapses. Handlers still
    /// running then are abandoned with a logged warning rather than an exception; their domain rows
    /// are still pending, so recovery picks them up on the next start. After this method is called,
    /// <see cref="ScheduleAsync"/> logs a warning and schedules nothing.
    /// </remarks>
    /// <param name="cancellationToken">Signalled when the host stops waiting for a graceful shutdown.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tickless stopping - cancelling {Count} scheduled job(s).", _scheduledJobs.Count);

        // Asynchronous so that callbacks handlers registered on the token run off this thread.
        await _stopping.CancelAsync().ConfigureAwait(false);

        foreach (var jobId in _scheduledJobs.Keys)
        {
            CancelInternal(jobId);
        }

        var executions = _executions.Keys.ToArray();
        if (executions.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(executions).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Tickless stopped before {Count} running job(s) finished. They run again on the next start if recovery still reports them.",
                executions.Count(e => !e.IsCompleted));
        }

        foreach (var execution in executions)
        {
            if (execution.IsCompleted)
            {
                _executions.TryRemove(execution, out _);
            }
        }
    }

    // A class rather than a record: entries are compared by reference, so a conditional remove
    // only ever removes the exact entry the caller armed.
    private sealed class ScheduledEntry
    {
        private readonly CancellationTokenSource _cts;

        public ScheduledEntry(DateTime executeAtUtc, string jobType, CancellationToken stopping)
        {
            ExecuteAtUtc = executeAtUtc;
            JobType = jobType;
            // Linked, so stopping the scheduler cancels every timer, including one armed by a
            // ScheduleAsync call that raced with StopAsync.
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            // Captured up front: reading CancellationTokenSource.Token after Dispose throws.
            Token = _cts.Token;
        }

        public DateTime ExecuteAtUtc { get; }
        public string JobType { get; }
        public CancellationToken Token { get; }

        public void Cancel()
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        public void Release() => _cts.Dispose();
    }
}
