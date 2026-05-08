namespace Tickless;

/// <summary>
/// Handles execution of a specific job type and provides startup recovery from domain state.
/// </summary>
/// <remarks>
/// Each handler is responsible for two things: executing jobs of its <see cref="JobType"/>,
/// and reporting outstanding work at startup so the scheduler can rehydrate timers after a restart.
/// Recovery queries should target the application's own domain tables, not a Tickless-owned schema:
/// Tickless does not persist anything itself.
/// </remarks>
public interface IJobHandler
{
    /// <summary>
    /// The job type identifier. Must be unique across all handlers registered in the application.
    /// </summary>
    string JobType { get; }

    /// <summary>
    /// Execute the job. The payload is the same string that was passed to
    /// <see cref="IJobScheduler.ScheduleAsync"/> when the job was scheduled.
    /// </summary>
    Task ExecuteAsync(string? payload);

    /// <summary>
    /// On application startup, scan domain state for outstanding work and return a job
    /// definition for each item that should be (re)scheduled. Called once per handler when
    /// the host starts.
    /// </summary>
    /// <remarks>
    /// This is the contract that makes Tickless durable across restarts. If a handler returns
    /// nothing, work scheduled before the restart is lost. Recovery queries are typically a
    /// one-line projection over a domain table with a <c>Status</c> or <c>DueAt</c> column.
    /// </remarks>
    Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync();
}
