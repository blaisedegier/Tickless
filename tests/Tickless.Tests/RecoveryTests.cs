using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tickless.Tests;

public class RecoveryTests
{
    private static InProcessJobScheduler CreateScheduler(params IJobHandler[] handlers)
        => new(handlers, NullLogger<InProcessJobScheduler>.Instance);

    [Fact]
    public async Task StartAsync_RehydratesPendingJobsFromHandler()
    {
        var jobs = new[]
        {
            new ScheduledJob("job-1", DateTime.UtcNow.AddSeconds(10), "p1"),
            new ScheduledJob("job-2", DateTime.UtcNow.AddSeconds(20), "p2"),
        };
        var handler = new TestJobHandler { RecoveryJobs = jobs };
        var scheduler = CreateScheduler(handler);

        await scheduler.StartAsync(default);

        var scheduled = scheduler.GetScheduled();
        scheduled.Should().HaveCount(2);
        scheduled.Select(s => s.JobId).Should().BeEquivalentTo(new[] { "job-1", "job-2" });
    }

    [Fact]
    public async Task StartAsync_RehydratesAcrossMultipleHandlers()
    {
        var handlerA = new TestJobHandler("TypeA")
        {
            RecoveryJobs = new[] { new ScheduledJob("a-1", DateTime.UtcNow.AddMinutes(1), null) }
        };
        var handlerB = new TestJobHandler("TypeB")
        {
            RecoveryJobs = new[]
            {
                new ScheduledJob("b-1", DateTime.UtcNow.AddMinutes(1), null),
                new ScheduledJob("b-2", DateTime.UtcNow.AddMinutes(2), null),
            }
        };
        var scheduler = CreateScheduler(handlerA, handlerB);

        await scheduler.StartAsync(default);

        scheduler.GetScheduled().Should().HaveCount(3);
        scheduler.GetScheduled().Where(s => s.JobType == "TypeA").Should().HaveCount(1);
        scheduler.GetScheduled().Where(s => s.JobType == "TypeB").Should().HaveCount(2);
    }

    [Fact]
    public async Task StartAsync_RecoveredJobFiresAtItsScheduledTime()
    {
        var handler = new TestJobHandler
        {
            RecoveryJobs = new[]
            {
                new ScheduledJob("recovered-1", DateTime.UtcNow.AddMilliseconds(100), "recovered-payload")
            }
        };
        var scheduler = CreateScheduler(handler);

        await scheduler.StartAsync(default);

        var executed = await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        executed.Should().Be("recovered-payload");
    }

    [Fact]
    public async Task StartAsync_PastDueRecoveredJob_DoesNotRunHandlerInline()
    {
        using var gate = new ManualResetEventSlim();
        var handler = new TestJobHandler
        {
            RecoveryJobs = new[] { new ScheduledJob("overdue", DateTime.UtcNow.AddHours(-1), "late") },
            // Blocks synchronously, so a handler running on the caller's thread would hold up StartAsync.
            OnExecute = (_, _) =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return Task.CompletedTask;
            },
        };
        var scheduler = CreateScheduler(handler);

        try
        {
            await Task.Run(() => scheduler.StartAsync(default)).WaitAsync(TimeSpan.FromSeconds(2));
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            gate.Set();
        }

        (await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("late");
    }

    [Fact]
    public async Task StartAsync_PassesTokenToRecovery()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);
        using var startup = new CancellationTokenSource();

        await scheduler.StartAsync(startup.Token);

        handler.RecoveryToken.Should().Be(startup.Token);
    }

    [Fact]
    public async Task StartAsync_CancelledToken_Throws()
    {
        var handler = new TestJobHandler
        {
            RecoveryJobs = new[] { new ScheduledJob("job-1", DateTime.UtcNow.AddMinutes(1), null) },
        };
        var scheduler = CreateScheduler(handler);
        using var startup = new CancellationTokenSource();
        startup.Cancel();

        var act = () => scheduler.StartAsync(startup.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        scheduler.GetScheduled().Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_CancelledDuringRecovery_Throws()
    {
        using var startup = new CancellationTokenSource();
        var handler = new TestJobHandler
        {
            RecoveryJobs = new[] { new ScheduledJob("job-1", DateTime.UtcNow.AddMinutes(1), null) },
            OnRecover = async ct =>
            {
                await startup.CancelAsync();
                ct.ThrowIfCancellationRequested();
            },
        };
        var scheduler = CreateScheduler(handler);

        var act = () => scheduler.StartAsync(startup.Token);

        // Rethrown, rather than logged as a failed recovery and skipped.
        await act.Should().ThrowAsync<OperationCanceledException>();
        scheduler.GetScheduled().Should().BeEmpty();
    }
}
