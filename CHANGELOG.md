# Changelog

All notable changes to Tickless. Versions follow [Semantic Versioning](https://semver.org/).

## 1.1.0 - 2026-11-10

The public API only grows in this release: `dotnet pack` validates it against 1.0.0 and finds no binary break. Changes in behaviour that callers can notice are listed under "Behaviour changes".

### Fixed

- A job rescheduled while its handler was running disappeared from `GetScheduled()` and could no longer be cancelled once the handler finished, although its timer still fired. Each execution now removes only the entry it armed.
- Concurrent `ScheduleAsync` calls for the same job id could leave a replaced timer running. Replacement is now atomic, so exactly one timer survives.
- An `OperationCanceledException` thrown by a handler itself (an `HttpClient` timeout, for example) was taken for the scheduler's own cancellation: it was not logged and the job stayed in `GetScheduled()` for good. It is now logged as a failure and the job is removed.
- A `DateTime` with `Kind=Local` fired early or late by the machine's UTC offset. Local values are now converted to UTC, `Unspecified` values are treated as UTC, and `GetScheduled()` always reports `Kind=Utc`.
- A `CancelAsync` or `ScheduleAsync` for the same id that landed just after a job's timer fired, but before its handler was called, still ran the old handler. The timer's token is now checked again before the handler is called.
- A job due now or in the past ran its handler on the caller's thread inside `ScheduleAsync`, so at startup every overdue handler ran in turn before the host finished starting. It now runs on the thread pool.

### Added

- `IJobHandler.ExecuteAsync(string?, CancellationToken)` and `IJobHandler.RecoverPendingJobsAsync(CancellationToken)`. The scheduler always calls these. Their default implementations forward to the 1.0 methods, so existing handlers compile and run unchanged.
- Graceful shutdown: `StopAsync` cancels every timer, signals the token passed to running handlers, and waits for them until the host's shutdown timeout, then logs how many it abandoned.
- `StartAsync` passes its cancellation token to recovery and throws `OperationCanceledException` when startup is cancelled.
- Package validation against 1.0.0 on every pack, including in CI.

### Behaviour changes

- `ScheduleAsync` with a job type that has no registered handler now throws `ArgumentException` (parameter `jobType`). In 1.0 it logged an error and silently dropped the job. The exception is thrown before an existing job with the same id is touched.
- `ScheduleAsync` called after `StopAsync` logs a warning and schedules nothing.
- Unit tests that mock `IJobHandler` and set up only the 1.0 overloads no longer reach those setups. The scheduler calls the `CancellationToken` overloads, and the mock's proxy intercepts them instead of running their default implementations. A loose Moq mock returns a completed task, so the job appears to succeed while the 1.0 setup never runs and `Verify` on it fails; a strict mock throws. Set up the token overload, or set `CallBase = true` so the default implementations forward to the 1.0 setups:

  ```csharp
  handler.Setup(h => h.ExecuteAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
         .Returns(Task.CompletedTask);
  // or
  var handler = new Mock<IJobHandler> { CallBase = true };
  ```

  A handler that returns `null` instead of a task from either token overload is treated as having nothing to do, with a warning logged once per job type, instead of failing every job. See "Upgrading from 1.0" in the README.

## 1.0.0 - 2026-05-11

First release.

- `IJobScheduler` with `ScheduleAsync`, `CancelAsync` and `GetScheduled`, implemented in process by `InProcessJobScheduler` on `Task.Delay`, with delays longer than about 24.9 days split into chunks.
- `IJobHandler` with `ExecuteAsync` and `RecoverPendingJobsAsync`; recovery rebuilds timers from domain state at startup.
- `AddTickless()` registers the scheduler as a singleton and as a hosted service.
- A blog-publishing sample over SQLite with a Scalar UI.
