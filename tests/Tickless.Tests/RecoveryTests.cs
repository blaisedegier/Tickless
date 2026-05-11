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
}
