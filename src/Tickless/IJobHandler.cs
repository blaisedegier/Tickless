namespace Tickless;

/// <summary>
/// Handles execution of a specific job type and provides startup recovery from domain state.
/// </summary>
/// <remarks>
/// <para>
/// Each handler is responsible for two things: executing jobs of its <see cref="JobType"/>,
/// and reporting outstanding work at startup so the scheduler can rehydrate timers after a restart.
/// Recovery queries should target the application's own domain tables, not a Tickless-owned schema:
/// Tickless does not persist anything itself.
/// </para>
/// <para>
/// The scheduler always calls the overloads that take a <see cref="CancellationToken"/>. Their
/// default implementations ignore the token and call the original overloads, so a handler written
/// against Tickless 1.0 keeps working unchanged. To observe cancellation, implement the token
/// overloads and forward the original ones to them, for example
/// <c>public Task ExecuteAsync(string? payload) => ExecuteAsync(payload, CancellationToken.None);</c>.
/// </para>
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
    /// <remarks>
    /// The original overload. The scheduler calls <see cref="ExecuteAsync(string, CancellationToken)"/>,
    /// whose default implementation forwards here.
    /// </remarks>
    /// <param name="payload">The payload the job was scheduled with.</param>
    Task ExecuteAsync(string? payload);

    /// <summary>
    /// Execute the job, observing <paramref name="cancellationToken"/>. This is the overload the
    /// scheduler calls.
    /// </summary>
    /// <remarks>
    /// The default implementation ignores the token and calls <see cref="ExecuteAsync(string)"/>.
    /// </remarks>
    /// <param name="payload">The payload the job was scheduled with.</param>
    /// <param name="cancellationToken">
    /// Signalled when the host is stopping. It is not signalled when the job is rescheduled or
    /// cancelled after the handler has started: both affect only the job's timer, never a handler
    /// that is already running.
    /// </param>
    Task ExecuteAsync(string? payload, CancellationToken cancellationToken) => ExecuteAsync(payload);

    /// <summary>
    /// On application startup, scan domain state for outstanding work and return a job
    /// definition for each item that should be (re)scheduled. Called once per handler when
    /// the host starts.
    /// </summary>
    /// <remarks>
    /// The original overload. The scheduler calls
    /// <see cref="RecoverPendingJobsAsync(CancellationToken)"/>, whose default implementation
    /// forwards here.
    /// </remarks>
    Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync();

    /// <summary>
    /// On application startup, scan domain state for outstanding work and return a job
    /// definition for each item that should be (re)scheduled. Called once per handler when
    /// the host starts. This is the overload the scheduler calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the contract that makes Tickless durable across restarts. If a handler returns
    /// nothing, work scheduled before the restart is lost. Recovery queries are typically a
    /// one-line projection over a domain table with a <c>Status</c> or <c>DueAt</c> column.
    /// </para>
    /// <para>
    /// The host does not finish starting until every handler has recovered, so keep the query
    /// cheap and indexed. The default implementation ignores the token and calls
    /// <see cref="RecoverPendingJobsAsync()"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Signalled when the host abandons startup.</param>
    Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync(CancellationToken cancellationToken)
        => RecoverPendingJobsAsync();
}
