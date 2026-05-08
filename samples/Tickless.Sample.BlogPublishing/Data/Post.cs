namespace Tickless.Sample.BlogPublishing.Data;

public class Post
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public DateTime PublishAtUtc { get; set; }
    public string Status { get; set; } = PostStatus.Scheduled;
}

public static class PostStatus
{
    public const string Scheduled = "Scheduled";
    public const string Published = "Published";
}
