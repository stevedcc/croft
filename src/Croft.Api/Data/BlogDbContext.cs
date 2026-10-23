using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Data;

public class BlogDbContext(DbContextOptions<BlogDbContext> options) : DbContext(options)
{
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<Feedback> Feedback => Set<Feedback>();
    public DbSet<PageView> PageViews => Set<PageView>();
    public DbSet<PostViewCount> PostViewCounts => Set<PostViewCount>();
    public DbSet<SocialThread> SocialThreads => Set<SocialThread>();
    public DbSet<SocialReply> SocialReplies => Set<SocialReply>();
    public DbSet<SocialBlock> SocialBlocks => Set<SocialBlock>();
    public DbSet<SocialAvatar> SocialAvatars => Set<SocialAvatar>();
    public DbSet<SocialAnnouncement> SocialAnnouncements => Set<SocialAnnouncement>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BlogDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
