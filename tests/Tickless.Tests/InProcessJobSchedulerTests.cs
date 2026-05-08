using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Tickless;

namespace Tickless.Tests;

public class InProcessJobSchedulerTests
{
    private static InProcessJobScheduler CreateScheduler(params IJobHandler[] handlers)
        => new(handlers, NullLogger<InProcessJobScheduler>.Instance);

    [Fact]
    public async Task ScheduleAsync_FiresHandlerAtScheduledTime()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddMilliseconds(100), "Test", "payload");

        var executed = await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        executed.Should().Be("payload");
    }

    [Fact]
    public async Task ScheduleAsync_PastDueTime_FiresImmediately()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddSeconds(-5), "Test", "payload");

        var executed = await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        executed.Should().Be("payload");
    }

    [Fact]
    public async Task ScheduleAsync_DuplicateId_CancelsPreviousAndFiresLatest()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddMilliseconds(500), "Test", "first");
        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddMilliseconds(100), "Test", "second");

        var executed = await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        executed.Should().Be("second");
        handler.Payloads.Should().ContainSingle().Which.Should().Be("second");
    }

    [Fact]
    public async Task CancelAsync_PreventsHandlerFromFiring()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddMilliseconds(500), "Test");
        await scheduler.CancelAsync("job-1");

        await Task.Delay(700);

        handler.Executed.Task.IsCompleted.Should().BeFalse();
        scheduler.GetScheduled().Should().BeEmpty();
    }

    [Fact]
    public async Task CancelAsync_UnknownJobId_IsNoOp()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        var act = () => scheduler.CancelAsync("never-scheduled");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ScheduleAsync_UnknownJobType_DoesNotSchedule()
    {
        var handler = new TestJobHandler("KnownType");
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddSeconds(10), "UnknownType");

        scheduler.GetScheduled().Should().BeEmpty();
    }

    [Fact]
    public async Task HandlerException_DoesNotCrashSchedulerAndJobIsRemoved()
    {
        var handler = new TestJobHandler { ShouldThrow = true };
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddMilliseconds(50), "Test");

        await Task.Delay(300);

        scheduler.GetScheduled().Should().BeEmpty();
    }

    [Fact]
    public async Task GetScheduled_ReturnsCurrentlyScheduledJobsAndReflectsCancellations()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddSeconds(10), "Test");
        await scheduler.ScheduleAsync("job-2", DateTime.UtcNow.AddSeconds(20), "Test");

        scheduler.GetScheduled().Should().HaveCount(2);
        scheduler.GetScheduled().Select(s => s.JobId).Should().BeEquivalentTo(new[] { "job-1", "job-2" });
        scheduler.GetScheduled().Should().AllSatisfy(s => s.JobType.Should().Be("Test"));

        await scheduler.CancelAsync("job-1");

        scheduler.GetScheduled().Should().ContainSingle().Which.JobId.Should().Be("job-2");
    }

    [Fact]
    public async Task StopAsync_CancelsAllScheduledJobs()
    {
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddSeconds(10), "Test");
        await scheduler.ScheduleAsync("job-2", DateTime.UtcNow.AddSeconds(10), "Test");

        await scheduler.StopAsync(default);

        scheduler.GetScheduled().Should().BeEmpty();
    }
}
