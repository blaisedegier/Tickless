namespace Tickless;

/// <summary>
/// A job definition returned from <see cref="IJobHandler.RecoverPendingJobsAsync(CancellationToken)"/> so the
/// scheduler can rehydrate its timer for that job at startup.
/// </summary>
/// <param name="JobId">Unique job identifier. Stable across application restarts is recommended.</param>
/// <param name="ExecuteAtUtc">
/// When the job should execute. Past values fire as soon as possible. A value read back from a
/// database column usually has <see cref="DateTimeKind.Unspecified"/>; it is treated as UTC.
/// </param>
/// <param name="Payload">Optional serialised data passed to <see cref="IJobHandler.ExecuteAsync(string, CancellationToken)"/>.</param>
public sealed record ScheduledJob(string JobId, DateTime ExecuteAtUtc, string? Payload);
