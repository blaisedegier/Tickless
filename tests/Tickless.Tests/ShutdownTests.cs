using AwesomeAssertions;
using Microsoft.Extensions.Logging;

namespace Tickless.Tests;

public class ShutdownTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static (InProcessJobScheduler Scheduler, RecordingLogger<InProcessJobScheduler> Logger) Create(
        IJobHandler handler)
    {
        var logger = new RecordingLogger<InProcessJobScheduler>();
        return (new InProcessJobScheduler(new[] { handler }, logger), logger);
    }

    [Fact]
    public async Task StopAsync_SignalsTokenToRunningHandler()
    {
        var handler = new TestJobHandler { OnExecute = (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct) };
        var (scheduler, logger) = Create(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow, "Test");
        await handler.Started.Task.WaitAsync(Patience);
        handler.ExecutionToken.IsCancellationRequested.Should().BeFalse();

        await scheduler.StopAsync(CancellationToken.None).WaitAsync(Patience);

        handler.ExecutionToken.IsCancellationRequested.Should().BeTrue();
        handler.Executed.Task.IsCompleted.Should().BeFalse();
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("interrupted by shutdown"));
        scheduler.InFlightExecutions.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_WaitsForInFlightHandler()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores the token, so only the gate lets it finish.
        var handler = new TestJobHandler { OnExecute = (_, _) => gate.Task };
        var (scheduler, _) = Create(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow, "Test", "payload");
        await handler.Started.Task.WaitAsync(Patience);

        var stop = scheduler.StopAsync(CancellationToken.None);
        await Task.WhenAny(stop, Task.Delay(TimeSpan.FromMilliseconds(100)));
        stop.IsCompleted.Should().BeFalse();
        scheduler.InFlightExecutions.Should().Be(1);

        gate.SetResult();
        await stop.WaitAsync(Patience);

        handler.Executed.Task.IsCompletedSuccessfully.Should().BeTrue();
        scheduler.InFlightExecutions.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_HonoursShutdownToken_WhenHandlerHangs()
    {
        var hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestJobHandler { OnExecute = (_, _) => hang.Task };
        var (scheduler, logger) = Create(handler);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow, "Test");
        await handler.Started.Task.WaitAsync(Patience);

        using var shutdown = new CancellationTokenSource();
        var stop = scheduler.StopAsync(shutdown.Token);
        await shutdown.CancelAsync();

        // Returns without throwing once the host gives up waiting.
        await stop.WaitAsync(Patience);
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("before 1 running job(s) finished"));
        scheduler.InFlightExecutions.Should().Be(1);

        hang.SetResult();
    }

    [Fact]
    public async Task Execution_IsTrackedBeforeItRuns_SoStopAsyncCannotMissIt()
    {
        var time = new ImmediateTimeProvider();
        var handler = new TestJobHandler();
        var scheduler = new InProcessJobScheduler(
            new[] { handler }, new RecordingLogger<InProcessJobScheduler>(), time);
        var trackedWhenFired = -1;
        // Runs inside ScheduleAsync, because the timer fires as soon as it is created. A StopAsync
        // taking its snapshot at this moment must already see the execution.
        scheduler.TimerFired = _ => trackedWhenFired = scheduler.InFlightExecutions;

        await scheduler.ScheduleAsync("job-1", time.GetUtcNow().UtcDateTime.AddMinutes(1), "Test");

        trackedWhenFired.Should().Be(1);
        await handler.Executed.Task.WaitAsync(Patience);
        await Wait.UntilAsync(() => scheduler.InFlightExecutions == 0);
    }

    [Fact]
    public async Task ScheduleAsync_AfterStop_DoesNotArm()
    {
        var handler = new TestJobHandler();
        var (scheduler, logger) = Create(handler);
        await scheduler.StopAsync(CancellationToken.None);

        await scheduler.ScheduleAsync("job-1", DateTime.UtcNow, "Test");
        await scheduler.ScheduleAsync("job-2", DateTime.UtcNow.AddMinutes(1), "Test");

        scheduler.GetScheduled().Should().BeEmpty();
        scheduler.InFlightExecutions.Should().Be(0);
        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(2);
        handler.Payloads.Should().BeEmpty();
    }
}
