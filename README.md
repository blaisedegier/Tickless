# Tickless

An event-driven, in-process job scheduler for .NET that owns no tables: jobs fire at their exact time, and after a restart each handler rebuilds its own timers from your domain rows.

[![NuGet](https://img.shields.io/nuget/v/Tickless.svg)](https://www.nuget.org/packages/Tickless)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](https://github.com/blaisedegier/Tickless/blob/main/LICENSE)
[![CI](https://github.com/blaisedegier/Tickless/actions/workflows/ci.yml/badge.svg)](https://github.com/blaisedegier/Tickless/actions/workflows/ci.yml)

## What it does

- Runs a piece of work at a given UTC instant: publish a post at `PublishAtUtc`, expire a trial at `TrialExpiresAtUtc`, lift a cooldown at `CooldownExpiresAtUtc`.
- Fires on a timer, not on a polling loop, so nothing runs between jobs.
- Survives restarts without a table of its own. On startup it asks each handler what is still pending, and the handler answers from the rows your application already stores.
- Replaces a job when you schedule the same id again, cancels by id, and lists what is armed (without payloads) for an admin endpoint.
- Passes a cancellation token to handlers and drains running work on shutdown.

## How it works

### Your domain rows are the queue

Most schedulers get durability by adding their own job, state and queue tables. Tickless does not persist anything. A scheduled job is an in-memory timer keyed by a job id. Durability comes from the contract on `IJobHandler`: at startup the scheduler calls each handler's `RecoverPendingJobsAsync`, the handler queries its own tables for outstanding work (typically a `Status` filter plus a due-time column), and the scheduler re-arms a timer for each result.

If the process dies while timers are armed, the next start runs the same query and the timers come back. Nothing extra needs migrating, backing up or keeping in sync, because the only state is the rows you were already saving.

### Delivery guarantees

Delivery is **at-least-once**, provided recovery reports every row that is still pending:

- A handler that throws is logged and the job is dropped from memory. There is no in-process retry or backoff. If the handler did not mark its row done, the row is still pending, so recovery schedules it again on the next start.
- A handler interrupted by shutdown is in the same position: it runs again on the next start.
- A handler can therefore run more than once for the same row (for example after sending an email but before saving the status change). **Handlers must be idempotent**: check the row's state before acting, as the sample does with `Published` posts.

Work that has no pending row behind it is not recovered. That is the trade-off for owning no schema.

### Cancellation and shutdown

`IJobHandler` has two pairs of methods. The scheduler always calls the overloads that take a `CancellationToken`:

```csharp
Task ExecuteAsync(string? payload, CancellationToken cancellationToken);
Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync(CancellationToken cancellationToken);
```

Both are default interface methods that ignore the token and call the original 1.0 methods, `ExecuteAsync(string?)` and `RecoverPendingJobsAsync()`. A handler written against 1.0 therefore compiles and runs unchanged. A new handler implements the token overloads and forwards the 1.0 ones to them, as shown below.

On shutdown the scheduler cancels every timer, signals the token passed to running handlers, and waits for them to finish. It waits for as long as the host allows, which is `HostOptions.ShutdownTimeout` (30 seconds by default). Handlers still running after that are abandoned with a logged warning; their rows are still pending, so they run again on the next start. Once stopping has begun, `ScheduleAsync` logs a warning and schedules nothing.

The handler's token is signalled only by shutdown. Rescheduling or cancelling a job affects its timer, never a handler that is already running.

### Times are UTC

`ScheduleAsync` and `ScheduledJob` take a `DateTime`. A value with `DateTimeKind.Local` is converted to UTC. A value with `DateTimeKind.Unspecified` is treated as UTC, because that is how EF Core and most ADO.NET providers read a UTC column back; rejecting it would break recovery. `GetScheduled()` always reports `Kind=Utc`. Store due times as UTC and this rule never surprises you.

### Errors at schedule time

Scheduling a job type that has no registered handler throws `ArgumentException` (parameter `jobType`), naming the registered types. It is thrown before anything else happens, so an existing job with the same id stays scheduled.

## Running it

### Install

```bash
dotnet add package Tickless
```

The package targets `net10.0` and depends only on the hosting, dependency injection and logging abstractions.

### Register Tickless and a handler

```csharp
// Program.cs
builder.Services.AddDbContext<BlogContext>(o => o.UseSqlite("Data Source=blog.db"));
builder.Services.AddTickless();
builder.Services.AddSingleton<IJobHandler, PublishPostJobHandler>();
```

### Implement the handler

Handlers are singletons, so they create a scope for scoped services such as a `DbContext`.

```csharp
public class PublishPostJobHandler(IServiceProvider services) : IJobHandler
{
    public string JobType => "PublishPost";

    public Task ExecuteAsync(string? payload) => ExecuteAsync(payload, CancellationToken.None);

    public async Task ExecuteAsync(string? payload, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();
        var post = await db.Posts.FindAsync([Guid.Parse(payload!)], cancellationToken);
        if (post is null || post.Status == "Published")
        {
            return; // Idempotent: a second run does nothing.
        }
        post.Status = "Published";
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync()
        => RecoverPendingJobsAsync(CancellationToken.None);

    public async Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();
        var pending = await db.Posts
            .Where(p => p.Status == "Scheduled")
            .Select(p => new { p.Id, p.PublishAtUtc })
            .ToListAsync(cancellationToken);
        return pending.Select(p => new ScheduledJob($"publish-{p.Id}", p.PublishAtUtc, p.Id.ToString()));
    }
}
```

Recovery runs inside the hosted service's `StartAsync`, which the host awaits while starting, so a slow recovery query delays startup. Keep it a cheap query over an indexed status or due-time column.

### Schedule, cancel and inspect

Anywhere `IJobScheduler` is injected:

```csharp
await scheduler.ScheduleAsync(
    jobId:        $"publish-{post.Id}",
    executeAtUtc: post.PublishAtUtc,
    jobType:      "PublishPost",
    payload:      post.Id.ToString());

await scheduler.CancelAsync($"publish-{post.Id}");

app.MapGet("/scheduler/jobs", (IJobScheduler s) => s.GetScheduled());
```

Scheduling an id that already exists replaces the earlier job. A time in the past fires as soon as possible, on the thread pool rather than the caller's thread. `GetScheduled()` returns `JobId`, `ExecuteAtUtc` and `JobType` for each armed timer; payloads are left out so the list is safe to expose to an admin page.

### Upgrading from 1.0

Handlers written against 1.0 compile and run unchanged, with two exceptions worth checking:

- Scheduling a job type that has no registered handler now throws `ArgumentException` instead of logging an error and dropping the job.
- **Mocked handlers in unit tests.** The scheduler calls the `CancellationToken` overloads. A mocking library's proxy intercepts those overloads as well, so their default implementations never run and a setup on the 1.0 overload is never reached. With Moq, a loose mock returns a completed task from the unset overload: the job looks successful, but the 1.0 setup's callback does not run and `Verify` on the 1.0 overload fails. A strict mock throws.

Set up the overload the scheduler calls, or let the mock run the default implementations, which forward to your existing 1.0 setups:

```csharp
var handler = new Mock<IJobHandler>();
handler.SetupGet(h => h.JobType).Returns("PublishPost");
handler.Setup(h => h.ExecuteAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
       .Returns(Task.CompletedTask);
// ...
handler.Verify(h => h.ExecuteAsync("42", It.IsAny<CancellationToken>()), Times.Once);

// Or keep the 1.0 setups and turn on CallBase:
var legacy = new Mock<IJobHandler> { CallBase = true };
legacy.SetupGet(h => h.JobType).Returns("PublishPost");
legacy.Setup(h => h.ExecuteAsync(It.IsAny<string?>())).Returns(Task.CompletedTask);
```

If a handler returns `null` instead of a task from either overload, the scheduler treats it as having nothing to do and logs a warning once per job type, rather than failing every job.

### The sample

[`samples/Tickless.Sample.BlogPublishing`](https://github.com/blaisedegier/Tickless/tree/main/samples/Tickless.Sample.BlogPublishing) is a minimal API over SQLite with a [Scalar](https://scalar.com) UI. Schedule a post a minute out, stop the process before it fires, start it again, and the timer is re-armed from the `Posts` table and the post publishes on time.

```bash
dotnet run --project samples/Tickless.Sample.BlogPublishing
```

Then open <http://localhost:5009/scalar/v1>. `POST /posts` takes `publishAt` as an ISO 8601 timestamp with an offset (for example `2026-11-10T14:30:00+02:00`) and stores it as UTC.

### Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/blaisedegier/Tickless.git
cd Tickless
dotnet build Tickless.slnx -c Release
dotnet test Tickless.slnx -c Release
dotnet pack src/Tickless/Tickless.csproj -c Release -o ./artifacts
```

`dotnet pack` runs package validation against the published 1.0.0 and fails on a binary-breaking API change. Releases are tag-driven: pushing a `v*` tag runs the tests, packs with the version taken from the tag, and publishes to NuGet. Changes per version are in the [changelog](https://github.com/blaisedegier/Tickless/blob/main/CHANGELOG.md).

## How it compares

These statements describe each project's default configuration as of November 2026; see each project's documentation for current behaviour.

- **Hangfire** always needs a job storage. Its persistent storages ([SQL Server, Redis](https://docs.hangfire.io/en/latest/configuration/index.html)) keep jobs in tables or keys that Hangfire creates and owns, and the server finds delayed jobs by [polling that storage, every 15 seconds by default](https://docs.hangfire.io/en/latest/background-methods/calling-methods-with-delay.html). It supports several servers sharing one storage and ships a dashboard.
- **Quartz.NET** defaults to [`RAMJobStore`](https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/job-stores.html), which keeps schedules in memory, so they are lost when the application stops or crashes. Durable scheduling uses `AdoJobStore`, which needs Quartz's own database tables.
- **Coravel** owns no schema. Its [scheduler](https://docs.coravel.net/Scheduler/) runs recurring tasks defined in code, and its [queue](https://docs.coravel.net/Queuing/) runs in memory, so queued work that has not run yet is lost on restart.
- **Tickless** owns no schema and does not poll. One-off jobs fire on their own timer, and durability comes from your domain rows through recovery. It runs in a single process only.

Hangfire, and Quartz.NET with a persistent store, both run across several instances, and Hangfire also retries failed jobs and has a dashboard; reach for them when you need that. Tickless fits a single-instance app whose deadlines already live in its own tables.

## Tech

.NET 10 class library depending only on `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions`. Tests use xUnit, AwesomeAssertions and `FakeTimeProvider`. The sample uses ASP.NET Core minimal APIs, EF Core with SQLite, and Scalar.

## Known limitations

- **Single instance only.** Two replicas of the host each fire every job. There is no leader election or distributed lock; use Hangfire, Quartz.NET clustering or a broker's scheduled messages for scale-out.
- **No retries.** A failed handler is logged and dropped until the next start; there is no backoff or in-process retry.
- **Best-effort precision.** Timers are `Task.Delay`, which is accurate to human scale (seconds and up) but not real-time under thread-pool starvation or long GC pauses.
- **Recovery is bounded by startup.** Every pending job is recovered during startup and holds a timer in memory until it fires, so a very large backlog costs startup time and memory.
- **Same process only.** A job runs in the process that scheduled it; cross-service work belongs on a message bus.
- **Every job needs a durable row.** Work with no pending row behind it cannot be recovered after a restart.
- **String payloads.** Serialise anything richer yourself.

## Licence

MIT. See [LICENSE](https://github.com/blaisedegier/Tickless/blob/main/LICENSE).
