using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace Tickless.Sample.BlogPublishing.OpenApi;

internal sealed class CreatePostExampleTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var desc = context.Description;
        if (desc.RelativePath != "posts" || !string.Equals(desc.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        if (operation.RequestBody?.Content?.TryGetValue("application/json", out var media) == true)
        {
            media.Example = JsonNode.Parse($$"""
                {
                  "title": "Hello from Tickless",
                  "body": "This post is scheduled to publish in about 60 seconds.",
                  "publishAt": "{{DateTimeOffset.UtcNow.AddSeconds(60):O}}"
                }
                """);
        }

        return Task.CompletedTask;
    }
}

internal sealed class TicklessInfoDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Tickless - Blog Publishing Sample",
            Version = "1.0.0",
            Description = TicklessIntroduction.Markdown,
            Contact = new OpenApiContact
            {
                Name = "Blaise De Gier",
                Url = new Uri("https://github.com/blaisedegier/Tickless")
            },
            License = new OpenApiLicense
            {
                Name = "MIT",
                Url = new Uri("https://opensource.org/licenses/MIT")
            }
        };

        document.Tags = new HashSet<OpenApiTag>
        {
            new()
            {
                Name = "Posts",
                Description = "Endpoints for creating and listing scheduled posts. Posting a future-dated post arms a Tickless timer; on restart the timer rearms by querying this same Posts table."
            },
            new()
            {
                Name = "Scheduler",
                Description = "Inspect what is currently armed in the in-process scheduler. The contents of this list are pure in-memory state. There is no scheduler-owned table behind it; after a restart, the list is rebuilt entirely from your domain rows."
            }
        };

        return Task.CompletedTask;
    }
}

internal static class TicklessIntroduction
{
    public const string Markdown = """
        ## A scheduler that owns no tables

        Most job schedulers solve durability by adding their own tables. A job table, a state table, a queue table, plus a few more. Tickless takes a different route: every deadline you care about already lives in your domain (`PublishAtUtc`, `TrialExpiresAtUtc`, `CooldownExpiresAtUtc`). On startup, each handler queries that domain state and tells the scheduler what is still outstanding.

        Your domain rows are the queue. Tickless owns no schema of its own.

        > [!NOTE]
        > **What this demo proves.** This API is a small blog-publishing service backed by SQLite. Posts have a `PublishAtUtc`. When you create one, Tickless arms an in-memory timer for that exact instant. Kill the API mid-wait, restart it, and the timer rearms automatically, because the same Posts table that records your scheduled posts is the same table the handler queries at startup.

        ## Try it in 60 seconds

        1. **POST `/posts`** with a `publishAt` 30-60 seconds in the future. The post lands in `Scheduled` state.
        2. **GET `/scheduler/jobs`** to confirm the timer is armed.
        3. **Hit Ctrl+C** in the terminal running this API.
        4. **Restart it.** Hit `/scheduler/jobs` again, the same job is back, rearmed from SQLite.
        5. **Wait.** When the timer fires, `/posts` shows the post flipped to `Published`.

        ## More

        - Package: [`Tickless` on NuGet](https://www.nuget.org/packages/Tickless)
        - Repo: [`Tickless` on GitHub](https://github.com/blaisedegier/Tickless)
        - The README compares Tickless with Hangfire, Quartz.NET and Coravel and lists its known limitations.
        """;
}

internal static class TicklessScalarCustomization
{
    public const string Css = """
        :root {
          --scalar-color-accent: #0891b2;
          --scalar-font: ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
        }
        .dark-mode {
          --scalar-color-accent: #22d3ee;
        }
        .scalar-app .markdown blockquote {
          border-left: 3px solid var(--scalar-color-accent);
          padding-left: 1rem;
        }
        .scalar-app .sidebar-heading {
          letter-spacing: 0.04em;
        }
        """;
}
