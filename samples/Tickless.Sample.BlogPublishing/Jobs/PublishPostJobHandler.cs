using Microsoft.EntityFrameworkCore;
using Tickless.Sample.BlogPublishing.Data;

namespace Tickless.Sample.BlogPublishing.Jobs;

/// <summary>
/// Flips a Post from Scheduled to Published when its scheduled time arrives,
/// and rehydrates pending jobs at startup by querying the Posts table.
/// </summary>
/// <remarks>
/// Implements the cancellation-aware overloads, which the scheduler calls, and forwards the
/// original overloads to them. Execution is idempotent: a post that is already published is left
/// alone, so running the job twice (delivery is at-least-once) does no harm.
/// </remarks>
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

    public Task ExecuteAsync(string? payload) => ExecuteAsync(payload, CancellationToken.None);

    public async Task ExecuteAsync(string? payload, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(payload, out var postId))
        {
            _logger.LogError("PublishPostJobHandler received invalid payload: '{Payload}'", payload);
            return;
        }

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();

        var post = await db.Posts.FindAsync([postId], cancellationToken);
        if (post is null)
        {
            _logger.LogWarning("Post {PostId} not found at execution time.", postId);
            return;
        }

        if (post.Status == PostStatus.Published)
        {
            _logger.LogInformation("Post {PostId} is already published.", postId);
            return;
        }

        post.Status = PostStatus.Published;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Published post {PostId} ('{Title}').", post.Id, post.Title);
    }

    public Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync()
        => RecoverPendingJobsAsync(CancellationToken.None);

    public async Task<IEnumerable<ScheduledJob>> RecoverPendingJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BlogContext>();

        var pending = await db.Posts
            .AsNoTracking()
            .Where(p => p.Status == PostStatus.Scheduled)
            .Select(p => new { p.Id, p.PublishAtUtc })
            .ToListAsync(cancellationToken);

        return pending.Select(p => new ScheduledJob(
            JobId: $"publish-{p.Id}",
            ExecuteAtUtc: p.PublishAtUtc,
            Payload: p.Id.ToString()));
    }
}
