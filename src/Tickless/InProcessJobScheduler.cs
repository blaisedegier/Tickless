using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Tickless;

/// <summary>
/// In-process implementation of <see cref="IJobScheduler"/> backed by
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.
/// Event-driven within a single process: jobs fire at their exact scheduled instant without polling.
/// On startup, recovers pending jobs from the database via each registered
/// <see cref="IJobHandler"/>'s <see cref="IJobHandler.RecoverPendingJobsAsync"/>.
/// </summary>
/// <remarks>
/// Single-instance only. Two replicas of the host will both fire every job. For horizontally
/// scaled deployments, pair this scheduler with a leader-election mechanism or use a queue-backed
/// alternative (Hangfire, Quartz, or Azure Service Bus scheduled messages).
/// </remarks>
public sealed class InProcessJobScheduler : IJobScheduler, IHostedService
{
    private readonly ConcurrentDictionary<string, ScheduledEntry> _scheduledJobs = new();
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
        if (!_handlers.ContainsKey(jobType))
        {
            _logger.LogError(
                "No handler registered for job type '{JobType}'. Job '{JobId}' will not be scheduled.",
                jobType, jobId);
            return Task.CompletedTask;
        }

        CancelInternal(jobId);

        var cts = new CancellationTokenSource();
        var entry = new ScheduledEntry(cts, executeAtUtc, jobType);
        _scheduledJobs[jobId] = entry;

        var delay = executeAtUtc - _time.GetUtcNow().UtcDateTime;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _logger.LogInformation(
            "Scheduled job '{JobId}' (type: {JobType}) to execute at {ExecuteAt:u} (in {Delay}).",
            jobId, jobType, executeAtUtc, delay);

        _ = ExecuteAfterDelayAsync(jobId, delay, jobType, payload, cts.Token);

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

    private bool CancelInternal(string jobId)
    {
        if (_scheduledJobs.TryRemove(jobId, out var entry))
        {
            entry.Cts.Cancel();
            entry.Cts.Dispose();
            return true;
        }
        return false;
    }

    private async Task ExecuteAfterDelayAsync(string jobId, TimeSpan delay, string jobType, string? payload, CancellationToken ct)
    {
        try
        {
            foreach (var chunk in ChunkDelay(delay))
            {
                await Task.Delay(chunk, _time, ct);
            }

            if (!_handlers.TryGetValue(jobType, out var handler))
            {
                _logger.LogError("Handler for job type '{JobType}' not found at execution time.", jobType);
                return;
            }

            _logger.LogInformation("Executing job '{JobId}' (type: {JobType}).", jobId, jobType);
            await handler.ExecuteAsync(payload);

            _scheduledJobs.TryRemove(jobId, out _);
            _logger.LogInformation("Job '{JobId}' completed successfully.", jobId);
        }
        catch (OperationCanceledException)
        {
            // Expected when ScheduleAsync replaces this job or CancelAsync is called.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job '{JobId}' (type: {JobType}) failed.", jobId, jobType);
            _scheduledJobs.TryRemove(jobId, out _);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tickless starting - recovering pending jobs from domain state.");

        foreach (var handler in _handlers.Values)
        {
            try
            {
                var pending = (await handler.RecoverPendingJobsAsync()).ToList();
                foreach (var job in pending)
                {
                    await ScheduleAsync(job.JobId, job.ExecuteAtUtc, handler.JobType, job.Payload);
                }

                if (pending.Count > 0)
                {
                    _logger.LogInformation(
                        "Recovered {Count} pending job(s) for handler '{JobType}'.",
                        pending.Count, handler.JobType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover jobs for handler '{JobType}'.", handler.JobType);
            }
        }

        _logger.LogInformation("Tickless started with {Count} handler(s) registered.", _handlers.Count);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Tickless stopping - cancelling {Count} scheduled job(s).", _scheduledJobs.Count);

        foreach (var kvp in _scheduledJobs)
        {
            kvp.Value.Cts.Cancel();
            kvp.Value.Cts.Dispose();
        }
        _scheduledJobs.Clear();

        return Task.CompletedTask;
    }

    private sealed record ScheduledEntry(CancellationTokenSource Cts, DateTime ExecuteAtUtc, string JobType);
}
