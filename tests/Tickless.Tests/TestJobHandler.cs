using System.Collections.Concurrent;

namespace Tickless.Tests;

internal sealed class TestJobHandler : IJobHandler
{
    public string JobType { get; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string?> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string?> Payloads { get; } = new();
    public IEnumerable<ScheduledJob> RecoveryJobs { get; init; } = Array.Empty<ScheduledJob>();
    public bool ShouldThrow { get; init; }

    /// <summary>Optional extra work run after the payload is recorded, for example waiting on a gate.</summary>
    public Func<string?, Task>? OnExecute { get; init; }

    public TestJobHandler(string jobType = "Test")
    {
        JobType = jobType;
    }

    public async Task ExecuteAsync(string? payload)
    {
        Payloads.Enqueue(payload);
        Started.TrySetResult();
        if (ShouldThrow)
        {
            throw new InvalidOperationException("Test handler configured to throw.");
        }
        if (OnExecute is not null)
        {
            await OnExecute(payload);
        }
        Executed.TrySetResult(payload);
    }

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync() => Task.FromResult(RecoveryJobs);
}
