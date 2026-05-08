using Microsoft.EntityFrameworkCore;

namespace Tickless.Sample.BlogPublishing.Data;

public class BlogContext : DbContext
{
    public BlogContext(DbContextOptions<BlogContext> options) : base(options) { }

    public DbSet<Post> Posts => Set<Post>();
}
