namespace Tickless;

/// <summary>
/// Schedules jobs to execute at specific UTC instants.
/// </summary>
/// <remarks>
/// Tickless deliberately owns no persistent storage. The scheduler is an in-memory cache
/// of timers; durability comes from each <see cref="IJobHandler"/>'s
/// <see cref="IJobHandler.RecoverPendingJobsAsync"/>, which reads pending work from the
/// caller's domain tables on application startup.
/// </remarks>
public interface IJobScheduler
{
    /// <summary>
    /// Schedule a job to execute at <paramref name="executeAtUtc"/>.
    /// If a job with the same <paramref name="jobId"/> already exists, it is cancelled and replaced.
    /// </summary>
    /// <param name="jobId">Unique identifier for this job. Used for cancellation and idempotent replacement.</param>
    /// <param name="executeAtUtc">When to execute the job. Times in the past fire as soon as possible.</param>
    /// <param name="jobType">Job type identifier; must match the <see cref="IJobHandler.JobType"/> of a registered handler.</param>
    /// <param name="payload">Optional serialised data passed to the handler at execution time.</param>
    Task ScheduleAsync(string jobId, DateTime executeAtUtc, string jobType, string? payload = null);

    /// <summary>
    /// Cancel a previously scheduled job. No-op if the job does not exist or has already executed.
    /// </summary>
    /// <param name="jobId">The id supplied when the job was scheduled.</param>
    Task CancelAsync(string jobId);

    /// <summary>
    /// Returns a snapshot of currently scheduled jobs. Intended for diagnostics, admin endpoints,
    /// and inspection UIs. The snapshot does not include payloads.
    /// </summary>
    IReadOnlyCollection<ScheduledJobView> GetScheduled();
}
