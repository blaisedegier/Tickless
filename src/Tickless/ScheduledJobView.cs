namespace Tickless;

/// <summary>
/// Read-only view of a currently scheduled job. Returned from
/// <see cref="IJobScheduler.GetScheduled"/> for diagnostics and inspection.
/// Payloads are intentionally omitted to avoid leaking job data through admin endpoints.
/// </summary>
/// <param name="JobId">The id the job was scheduled with.</param>
/// <param name="ExecuteAtUtc">The UTC instant at which the job will fire.</param>
/// <param name="JobType">The handler type that will receive the job.</param>
public sealed record ScheduledJobView(string JobId, DateTime ExecuteAtUtc, string JobType);
