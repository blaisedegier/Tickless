using System.Collections.Concurrent;

namespace Tickless.Tests;

/// <summary>A handler that implements the cancellation-aware overloads, as new handlers should.</summary>
internal sealed class TestJobHandler : IJobHandler
{
    public string JobType { get; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<string?> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<string?> Payloads { get; } = new();
    public IEnumerable<ScheduledJob> RecoveryJobs { get; init; } = Array.Empty<ScheduledJob>();
    public bool ShouldThrow { get; init; }
    public CancellationToken ExecutionToken { get; private set; }
    public CancellationToken RecoveryToken { get; private set; }

    /// <summary>Optional extra work run after the payload is recorded, for example waiting on a gate.</summary>
    public Func<string?, CancellationToken, Task>? OnExecute { get; init; }

    /// <summary>Optional work run during recovery, before the recovery jobs are returned.</summary>
    public Func<CancellationToken, Task>? OnRecover { get; init; }

    public TestJobHandler(string jobType = "Test")
    {
        JobType = jobType;
    }

    public Task ExecuteAsync(string? payload) => ExecuteAsync(payload, CancellationToken.None);

    public async Task ExecuteAsync(string? payload, CancellationToken cancellationToken)
    {
        ExecutionToken = cancellationToken;
        Payloads.Enqueue(payload);
        Started.TrySetResult();
        if (ShouldThrow)
        {
            throw new InvalidOperationException("Test handler configured to throw.");
        }
        if (OnExecute is not null)
        {
            await OnExecute(payload, cancellationToken);
        }
        Executed.TrySetResult(payload);
    }

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync() => RecoverPendingJobsAsync(CancellationToken.None);

    public async Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync(CancellationToken cancellationToken)
    {
        RecoveryToken = cancellationToken;
        if (OnRecover is not null)
        {
            await OnRecover(cancellationToken);
        }
        return RecoveryJobs;
    }
}

/// <summary>A handler written against Tickless 1.0: it implements only the original overloads.</summary>
internal sealed class LegacyJobHandler : IJobHandler
{
    public string JobType => "Legacy";
    public TaskCompletionSource<string?> Executed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IEnumerable<ScheduledJob> RecoveryJobs { get; init; } = Array.Empty<ScheduledJob>();

    public Task ExecuteAsync(string? payload)
    {
        Executed.TrySetResult(payload);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync() => Task.FromResult(RecoveryJobs);
}

/// <summary>
/// Behaves like a mocking proxy that intercepts the token overloads without calling their default
/// implementations and has nothing set up for them, so they return null.
/// </summary>
internal sealed class NullReturningJobHandler : IJobHandler
{
    public NullReturningJobHandler(string jobType)
    {
        JobType = jobType;
    }

    public string JobType { get; }

    public Task ExecuteAsync(string? payload) => Task.CompletedTask;

    public Task ExecuteAsync(string? payload, CancellationToken cancellationToken) => null!;

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync()
        => Task.FromResult(Enumerable.Empty<ScheduledJob>());

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync(CancellationToken cancellationToken) => null!;
}
