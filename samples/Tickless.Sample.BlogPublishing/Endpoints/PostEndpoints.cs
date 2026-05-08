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
                PublishAtUtc = request.PublishAtUtc,
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
        .WithSummary("Create a post scheduled for future publication.");

        app.MapGet("/posts", async (BlogContext db) =>
            await db.Posts.OrderBy(p => p.PublishAtUtc).ToListAsync())
        .WithName("ListPosts")
        .WithSummary("List all posts with their current status.");

        app.MapGet("/scheduler/jobs", (IJobScheduler scheduler) =>
            scheduler.GetScheduled())
        .WithName("InspectScheduledJobs")
        .WithSummary("Snapshot of jobs currently armed in the in-process scheduler.");

        return app;
    }
}

public record CreatePostRequest(string Title, string Body, DateTime PublishAtUtc);
