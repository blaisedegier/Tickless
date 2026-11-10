using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Tickless.Tests;

/// <summary>
/// A mock of <see cref="IJobHandler"/> set up only for the 1.0 overloads can return null from the
/// overloads the scheduler calls. The scheduler treats that as "nothing to do", not as a failure.
/// </summary>
public class NullReturningHandlerTests
{
    [Fact]
    public async Task NullTaskFromExecute_IsTreatedAsCompleted_AndWarnsOncePerJobType()
    {
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger<InProcessJobScheduler>();
        var scheduler = new InProcessJobScheduler(
            new IJobHandler[] { new NullReturningJobHandler("A"), new NullReturningJobHandler("B") },
            logger,
            time);
        var dueAt = time.GetUtcNow().UtcDateTime.AddSeconds(1);

        await scheduler.ScheduleAsync("a-1", dueAt, "A");
        await scheduler.ScheduleAsync("a-2", dueAt, "A");
        await scheduler.ScheduleAsync("b-1", dueAt, "B");
        time.Advance(TimeSpan.FromSeconds(1));
        await Wait.UntilAsync(() => scheduler.InFlightExecutions == 0);

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        logger.Entries.Where(e => e.Message.Contains("completed successfully")).Should().HaveCount(3);
        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("returned null")).ToList();
        warnings.Should().HaveCount(2);
        warnings.Should().ContainSingle(e => e.Message.Contains("'A'"));
        warnings.Should().ContainSingle(e => e.Message.Contains("'B'"));
        scheduler.GetScheduled().Should().BeEmpty();
    }

    [Fact]
    public async Task NullTaskFromRecovery_IsTreatedAsEmpty_AndOtherHandlersStillRecover()
    {
        var logger = new RecordingLogger<InProcessJobScheduler>();
        var recovering = new TestJobHandler("Real")
        {
            RecoveryJobs = new[] { new ScheduledJob("real-1", DateTime.UtcNow.AddMinutes(5), null) },
        };
        var scheduler = new InProcessJobScheduler(
            new IJobHandler[] { new NullReturningJobHandler("Mocked"), recovering },
            logger,
            TimeProvider.System);

        await scheduler.StartAsync(default);

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("'Mocked'") && e.Message.Contains("returned null"));
        scheduler.GetScheduled().Should().ContainSingle().Which.JobId.Should().Be("real-1");

        await scheduler.StopAsync(default);
    }
}
