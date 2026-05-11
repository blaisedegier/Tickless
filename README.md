# Tickless

> An event-driven, in-process job scheduler for .NET that owns no tables.

[![NuGet](https://img.shields.io/nuget/v/Tickless.svg)](https://www.nuget.org/packages/Tickless)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-blueviolet.svg)](https://dotnet.microsoft.com/)
[![CI](https://github.com/blaisedegier/Tickless/actions/workflows/ci.yml/badge.svg)](https://github.com/blaisedegier/Tickless/actions/workflows/ci.yml)

<!-- hero image: replace with Carbon-style "before / after" code snippet before announcing -->

## 📑 Table of Contents

- [💡 The Idea](#-the-idea)
- [📦 Install](#-install)
- [⚡ Quick Start](#-quick-start)
- [🔁 How Recovery Works](#-how-recovery-works)
- [🔍 Inspecting Scheduled Jobs](#-inspecting-scheduled-jobs)
- [⚖️ Comparison](#️-comparison)
- [🚫 When NOT to Use This](#-when-not-to-use-this)
- [🎬 Demo](#-demo)
- [📁 Project Structure](#-project-structure)
- [🛠️ Building from Source](#️-building-from-source)
- [📄 License](#-license)

## 💡 The Idea

Most job schedulers solve durability by adding their own tables. A job table, a state table, a queue table, plus a few more on top. Tickless takes a different route. The deadline you care about almost always already lives in your domain: `TrialExpiresAtUtc`, `PublishAtUtc`, `CooldownExpiresAtUtc`, and so on. On startup, each handler queries that domain state and tells the scheduler what is still outstanding. Tickless rehydrates its timers from there. There is nothing extra to migrate, nothing extra to back up, and nothing else to keep in sync.

In short: your domain state is the queue.

## 📦 Install

```bash
dotnet add package Tickless
```

Targets `net10.0`. The package brings in only the abstractions for hosting, DI, and logging, so it adds no infrastructure dependencies of its own.

## ⚡ Quick Start

### 1. Register Tickless and your handler

```csharp
// Program.cs
builder.Services.AddDbContext<BlogContext>(o => o.UseSqlite("Data Source=blog.db"));
builder.Services.AddTickless();
builder.Services.AddSingleton<IJobHandler, PublishPostJobHandler>();
```

### 2. Implement the handler

```csharp
public class PublishPostJobHandler(IServiceProvider services) : IJobHandler
{
    public string JobType => "PublishPost";

    public async Task ExecuteAsync(string? payload)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();
        var post = await db.Posts.FindAsync(Guid.Parse(payload!));
        post!.Status = "Published";
        await db.SaveChangesAsync();
    }

    public async Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync()
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();
        return (await db.Posts.Where(p => p.Status == "Scheduled").ToListAsync())
            .Select(p => new ScheduledJob($"publish-{p.Id}", p.PublishAtUtc, p.Id.ToString()));
    }
}
```

### 3. Schedule work

Anywhere you have `IJobScheduler` injected:

```csharp
await scheduler.ScheduleAsync(
    jobId:        $"publish-{post.Id}",
    executeAtUtc: post.PublishAtUtc,
    jobType:      "PublishPost",
    payload:      post.Id.ToString());
```

That is the whole API surface for scheduling. Cancel by id with `CancelAsync(jobId)`; replacement is idempotent (re-scheduling the same id cancels the previous timer).

## 🔁 How Recovery Works

When the host starts, Tickless calls `RecoverPendingJobsAsync` on every registered handler. Each handler returns the work it knows is still outstanding, derived from a query against its own domain tables. Tickless rearms timers for those jobs and resumes.

If the process dies while jobs are armed, restarting it re-runs the same query and the timers come back. There is no in-flight state to corrupt, because there is no in-flight state at all. Only the domain rows you were already saving.

## 🔍 Inspecting Scheduled Jobs

```csharp
app.MapGet("/scheduler/jobs", (IJobScheduler scheduler) => scheduler.GetScheduled());
```

`GetScheduled()` returns a snapshot of currently armed timers as `IReadOnlyCollection<ScheduledJobView>` (`JobId`, `ExecuteAtUtc`, `JobType`). Payloads are intentionally omitted so this is safe to wire to an admin endpoint.

## ⚖️ Comparison

|                            |     Tickless      |    Hangfire    |    Quartz.NET    | Coravel | Raw BackgroundService |
| :------------------------- | :---------------: | :------------: | :--------------: | :-----: | :-------------------: |
| Owns a database schema     |        No         |   Yes (many)   |       Yes        |   Yes   |          No           |
| Polls between jobs         |        No         |      Yes       |       Yes        |   Yes   |       Up to you       |
| Recovers across restarts   | Yes (your domain) |      Yes       |       Yes        |   Yes   |          No           |
| Multi-instance / scale-out |        No         |      Yes       |       Yes        |   No    |          No           |
| Built-in inspection        |     Endpoint      |    Full UI     |     Limited      | Limited |         None          |
| External infrastructure    |       None        | Optional Redis | Optional cluster |  None   |         None          |

## 🚫 When NOT to Use This

- **You run more than one host instance.** Two replicas will each fire every job. Tickless has no leader election or distributed locking. Reach for Hangfire or Quartz, or a queue-backed scheduler (Azure Service Bus or AWS SQS scheduled messages) for horizontally scaled deployments.
- **You need sub-second precision.** `Task.Delay` is best-effort under thread-pool starvation and GC pauses. Accuracy at human scale (seconds, minutes, days) is fine. Real-time systems are not.
- **You need cross-service messaging.** Tickless schedules work for a handler in the same process. If service A schedules work that should run on service B, use a message bus.
- **Your deadlines do not exist as durable rows.** The whole pattern hinges on each handler being able to ask "what is pending?" against your domain. Without a domain row backing each job, recovery has nothing to read.

## 🎬 Demo

A runnable sample lives in [`samples/Tickless.Sample.BlogPublishing`](./samples/Tickless.Sample.BlogPublishing) - a minimal API with SQLite and a [Scalar](https://scalar.com) UI. Schedule a post for 60 seconds out, hit Ctrl+C mid-wait, restart, watch the timer rearm and the post publish on time.

```bash
dotnet run --project samples/Tickless.Sample.BlogPublishing
```

Then open <http://localhost:5009/scalar/v1>. The Scalar page doubles as the demo's introduction, with a "Try it in 60 seconds" walkthrough that exercises the recovery path directly from the browser.

<!-- demo gif: record once and embed before announcing -->

## 📁 Project Structure

```text
Tickless/
  src/
    Tickless/                              The package
      IJobScheduler.cs                     Public contract: ScheduleAsync, CancelAsync, GetScheduled
      IJobHandler.cs                       Per-job-type contract: ExecuteAsync, RecoverPendingJobsAsync
      ScheduledJob.cs                      Recovery DTO returned from RecoverPendingJobsAsync
      ScheduledJobView.cs                  Read-only inspection record returned from GetScheduled
      InProcessJobScheduler.cs             ConcurrentDictionary + Task.Delay implementation
      ServiceCollectionExtensions.cs       AddTickless() DI helper
  samples/
    Tickless.Sample.BlogPublishing/        Minimal API demo (SQLite + EF Core + Scalar UI)
  tests/
    Tickless.Tests/                        xUnit + AwesomeAssertions (18 tests)
  .github/workflows/
    ci.yml                                 Build + test on push and PR
    release.yml                            Pack + publish on v* tags
```

## 🛠️ Building from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/blaisedegier/Tickless.git
cd Tickless
dotnet build
dotnet test
```

To produce the NuGet package locally:

```bash
dotnet pack src/Tickless/Tickless.csproj -c Release -o ./artifacts
```

The release pipeline is tag-driven. Push a tag matching `v*` (e.g. `v1.0.1`) and GitHub Actions builds, tests, packs with the version derived from the tag, and pushes to NuGet. The repo secret `NUGET_API_KEY` must be set.

## 📄 License

MIT - see [LICENSE](LICENSE).
