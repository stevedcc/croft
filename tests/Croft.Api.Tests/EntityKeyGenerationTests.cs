using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Tests;

public class EntityKeyGenerationTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task SavedEntities_WithoutId_GetUuidV7Keys()
    {
        var connectionString = fixture.ConnectionString;
        await using var db = new BlogDbContext(
            new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql(connectionString).Options);

        var now = DateTime.UtcNow;
        var post = new Post
        {
            Slug = "probe-post",
            Title = "Probe",
            MarkdownContent = "content",
            CreatedAt = now,
            UpdatedAt = now
        };
        var category = new Category { Name = "Probe", Slug = "probe-category" };
        var tag = new Tag { Name = "Probe", Slug = "probe-tag" };
        var mediaAsset = new MediaAsset
        {
            FileName = "probe.png",
            OriginalName = "probe.png",
            ContentType = "image/png",
            FilePath = "global/probe.png",
            UploadedAt = now,
            UploadedBy = "probe"
        };
        var feedback = new Feedback
        {
            Post = post,
            AuthorId = "probe",
            AuthorName = "Probe",
            QuotedText = "quoted",
            Comment = "comment",
            CreatedAt = now
        };
        var pageView = new PageView
        {
            Post = post,
            Timestamp = now,
            Day = DateOnly.FromDateTime(now),
            IpHash = "probe-hash"
        };

        db.AddRange(post, category, tag, mediaAsset, feedback, pageView);
        await db.SaveChangesAsync();

        post.Id.Version.Should().Be(7, "Post keys are UUIDv7");
        category.Id.Version.Should().Be(7, "Category keys are UUIDv7");
        tag.Id.Version.Should().Be(7, "Tag keys are UUIDv7");
        mediaAsset.Id.Version.Should().Be(7, "MediaAsset keys are UUIDv7");
        feedback.Id.Version.Should().Be(7, "Feedback keys are UUIDv7");
        pageView.Id.Version.Should().Be(7, "PageView keys are UUIDv7");
    }
}
