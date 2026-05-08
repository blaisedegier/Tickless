using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Tickless;
using Tickless.Sample.BlogPublishing.Data;
using Tickless.Sample.BlogPublishing.Endpoints;
using Tickless.Sample.BlogPublishing.Jobs;
using Tickless.Sample.BlogPublishing.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<BlogContext>(options =>
    options.UseSqlite("Data Source=blog.db"));

builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<TicklessInfoDocumentTransformer>();
});

builder.Services.AddTickless();
builder.Services.AddSingleton<IJobHandler, PublishPostJobHandler>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BlogContext>();
    db.Database.EnsureCreated();
}

app.MapOpenApi();
app.MapScalarApiReference(options =>
{
    options.Title = "Tickless — Live Demo";
    options.Theme = ScalarTheme.Default;
    options.DarkMode = true;
    options.CustomCss = TicklessScalarCustomization.Css;
});

app.MapGet("/", () => Results.Redirect("/scalar/v1")).ExcludeFromDescription();

app.MapPostEndpoints();

app.Run();
