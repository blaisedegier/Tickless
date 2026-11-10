using Microsoft.EntityFrameworkCore;
using Tickless.Sample.BlogPublishing.Data;
using Tickless.Sample.BlogPublishing.Jobs;

namespace Tickless.Sample.BlogPublishing.Endpoints;

public static class PostEndpoints
{
    public static IEndpointRouteBuilder MapPostEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/posts", async (CreatePostRequest request, BlogContext db, IJobScheduler scheduler) =>
        {
            var post = new Post
            {
                Id = Guid.NewGuid(),
                Title = request.Title,
                Body = request.Body,
                // The request carries an offset, so any time zone works; storage is always UTC.
                PublishAtUtc = request.PublishAt.UtcDateTime,
                Status = PostStatus.Scheduled
            };

            db.Posts.Add(post);
            await db.SaveChangesAsync();

            await scheduler.ScheduleAsync(
                jobId: $"publish-{post.Id}",
                executeAtUtc: post.PublishAtUtc,
                jobType: PublishPostJobHandler.Type,
                payload: post.Id.ToString());

            return Results.Created($"/posts/{post.Id}", post);
        })
        .WithName("CreateScheduledPost")
        .WithTags("Posts")
        .WithSummary("Create a scheduled post")
        .WithDescription("""
            Persists a `Post` in `Scheduled` state and arms a Tickless timer for the supplied
            `publishAt`, an ISO 8601 timestamp with an offset (for example `2026-11-10T14:30:00+02:00`
            or `2026-11-10T12:30:00Z`). It is stored as UTC. The job id (`publish-{postId}`) is stable
            across restarts, so if the process dies before the timer fires, recovery picks up exactly
            where it left off.

            Try a `publishAt` 30-60 seconds in the future, then poll `/scheduler/jobs`.
            """);

        app.MapGet("/posts", async (BlogContext db) =>
            await db.Posts.OrderBy(p => p.PublishAtUtc).ToListAsync())
        .WithName("ListPosts")
        .WithTags("Posts")
        .WithSummary("List all posts")
        .WithDescription("Returns every post regardless of status. Useful for confirming that a post flipped from `Scheduled` to `Published` after its timer fired.");

        app.MapGet("/scheduler/jobs", (IJobScheduler scheduler) =>
            scheduler.GetScheduled())
        .WithName("InspectScheduledJobs")
        .WithTags("Scheduler")
        .WithSummary("Snapshot of armed timers")
        .WithDescription("""
            Returns every job currently armed in the in-process scheduler, ordered as the
            internal map enumerates them. Payloads are deliberately omitted so this endpoint
            can be wired to an admin UI without leaking job data.

            Hit this immediately after restarting the API to see the recovery path in action.
            """);

        return app;
    }
}

public record CreatePostRequest(string Title, string Body, DateTimeOffset PublishAt);
