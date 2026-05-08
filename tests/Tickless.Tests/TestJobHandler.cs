using Tickless;

namespace Tickless.Tests;

internal sealed class TestJobHandler : IJobHandler
{
    public string JobType { get; }
    public TaskCompletionSource<string?> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<string?> Payloads { get; } = new();
    public IEnumerable<ScheduledJob> RecoveryJobs { get; init; } = Array.Empty<ScheduledJob>();
    public bool ShouldThrow { get; init; }

    public TestJobHandler(string jobType = "Test")
    {
        JobType = jobType;
    }

    public Task ExecuteAsync(string? payload)
    {
        Payloads.Add(payload);
        if (ShouldThrow)
        {
            throw new InvalidOperationException("Test handler configured to throw.");
        }
        Executed.TrySetResult(payload);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync() => Task.FromResult(RecoveryJobs);
}
