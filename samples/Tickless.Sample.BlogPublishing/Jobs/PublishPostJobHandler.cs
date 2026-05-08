using Microsoft.EntityFrameworkCore;
using Tickless.Sample.BlogPublishing.Data;

namespace Tickless.Sample.BlogPublishing.Jobs;

/// <summary>
/// Flips a Post from Scheduled to Published when its scheduled time arrives,
/// and rehydrates pending jobs at startup by querying the Posts table.
/// </summary>
public class PublishPostJobHandler : IJobHandler
{
    public const string Type = "PublishPost";

    private readonly IServiceProvider _services;
    private readonly ILogger<PublishPostJobHandler> _logger;

    public PublishPostJobHandler(IServiceProvider services, ILogger<PublishPostJobHandler> logger)
    {
        _services = services;
        _logger = logger;
    }

    public string JobType => Type;

    public async Task ExecuteAsync(string? payload)
    {
        if (!Guid.TryParse(payload, out var postId))
        {
            _logger.LogError("PublishPostJobHandler received invalid payload: '{Payload}'", payload);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();

        var post = await db.Posts.FindAsync(postId);
        if (post is null)
        {
            _logger.LogWarning("Post {PostId} not found at execution time.", postId);
            return;
        }

        post.Status = PostStatus.Published;
        await db.SaveChangesAsync();

        _logger.LogInformation("Published post {PostId} ('{Title}').", post.Id, post.Title);
    }

    public async Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();

        var pending = await db.Posts
            .AsNoTracking()
            .Where(p => p.Status == PostStatus.Scheduled)
            .Select(p => new { p.Id, p.PublishAtUtc })
            .ToListAsync();

        return pending.Select(p => new ScheduledJob(
            JobId: $"publish-{p.Id}",
            ExecuteAtUtc: p.PublishAtUtc,
            Payload: p.Id.ToString()));
    }
}
