using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Tickless.Tests;

public class InProcessJobSchedulerTests
{
    private static InProcessJobScheduler CreateScheduler(params IJobHandler[] handlers)
        => new(handlers, NullLogger<InProcessJobScheduler>.Instance);

    private static InProcessJobScheduler CreateScheduler(TimeProvider time, params IJobHandler[] handlers)
        => new(handlers, NullLogger<InProcessJobScheduler>.Instance, time);

    private static InProcessJobScheduler CreateScheduler(
        TimeProvider time, ILogger<InProcessJobScheduler> logger, params IJobHandler[] handlers)
        => new(handlers, logger, time);

    private static DateTime Now(TimeProvider time) => time.GetUtcNow().UtcDateTime;

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
        var time = new FakeTimeProvider();
        var handler = new TestJobHandler();
        var scheduler = CreateScheduler(time, handler);

        await scheduler.ScheduleAsync("job-1", Now(time).AddMilliseconds(500), "Test");
        await scheduler.CancelAsync("job-1");

        time.Advance(TimeSpan.FromSeconds(1));

        handler.Payloads.Should().BeEmpty();
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
    public async Task ScheduleAsync_UnknownJobType_Throws()
    {
        var handler = new TestJobHandler("KnownType");
        var scheduler = CreateScheduler(handler);

        var act = () => scheduler.ScheduleAsync("job-1", DateTime.UtcNow.AddSeconds(10), "UnknownType");

        (await act.Should().ThrowAsync<ArgumentException>())
            .WithParameterName("jobType")
            .WithMessage("*'UnknownType'*'KnownType'*");
        scheduler.GetScheduled().Should().BeEmpty();
    }

    [Fact]
    public async Task ScheduleAsync_UnknownJobType_DoesNotCancelExistingJobWithSameId()
    {
        var time = new FakeTimeProvider();
        var handler = new TestJobHandler("KnownType");
        var scheduler = CreateScheduler(time, handler);
        var dueAt = Now(time).AddSeconds(10);

        await scheduler.ScheduleAsync("job-1", dueAt, "KnownType", "payload");
        var act = () => scheduler.ScheduleAsync("job-1", Now(time).AddSeconds(20), "UnknownType");
        await act.Should().ThrowAsync<ArgumentException>();

        scheduler.GetScheduled().Should().ContainSingle()
            .Which.Should().Be(new ScheduledJobView("job-1", dueAt, "KnownType"));

        time.Advance(TimeSpan.FromSeconds(10));
        (await handler.Executed.Task.WaitAsync(TimeSpan.FromSeconds(2))).Should().Be("payload");
    }

    [Fact]
    public async Task HandlerException_DoesNotCrashSchedulerAndJobIsRemoved()
    {
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger<InProcessJobScheduler>();
        var handler = new TestJobHandler { ShouldThrow = true };
        var scheduler = CreateScheduler(time, logger, handler);

        await scheduler.ScheduleAsync("job-1", Now(time).AddMilliseconds(50), "Test");
        scheduler.GetScheduled().Should().ContainSingle();

        time.Advance(TimeSpan.FromMilliseconds(50));

        await Wait.UntilAsync(() => scheduler.GetScheduled().Count == 0);
        handler.Payloads.Should().ContainSingle();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task Reschedule_WhileHandlerRunning_NewEntryRemainsVisibleAndCancellable()
    {
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger<InProcessJobScheduler>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestJobHandler { OnExecute = _ => gate.Task };
        var scheduler = CreateScheduler(time, logger, handler);

        await scheduler.ScheduleAsync("job-1", Now(time), "Test", "first");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var rescheduledFor = Now(time).AddMinutes(5);
        await scheduler.ScheduleAsync("job-1", rescheduledFor, "Test", "second");

        gate.SetResult();
        await Wait.UntilAsync(() => logger.Entries.Any(e => e.Message.Contains("'job-1' completed")));

        scheduler.GetScheduled().Should().ContainSingle()
            .Which.ExecuteAtUtc.Should().Be(rescheduledFor);

        await scheduler.CancelAsync("job-1");
        time.Advance(TimeSpan.FromMinutes(10));

        scheduler.GetScheduled().Should().BeEmpty();
        handler.Payloads.Should().ContainSingle().Which.Should().Be("first");
    }

    [Fact]
    public async Task HandlerThrowsOwnOperationCanceled_IsTreatedAsFailureAndRemoved()
    {
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger<InProcessJobScheduler>();
        // For example an HttpClient timeout, which surfaces as TaskCanceledException.
        var handler = new TestJobHandler { OnExecute = _ => throw new TaskCanceledException("timed out") };
        var scheduler = CreateScheduler(time, logger, handler);

        await scheduler.ScheduleAsync("job-1", Now(time).AddSeconds(1), "Test");
        time.Advance(TimeSpan.FromSeconds(1));

        await Wait.UntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Error));
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error)
            .Which.Exception.Should().BeOfType<TaskCanceledException>();
        await Wait.UntilAsync(() => scheduler.GetScheduled().Count == 0);
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
