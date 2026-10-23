using AwesomeAssertions;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Tests;

/// <summary>Brief 13i item 3 (M-5): the analytics SQL names come from the EF model, once.</summary>
public sealed class AnalyticsTablesTests
{
    [Fact]
    public void ForTheSameModel_ReturnsTheSameInstance_SoTheNamesAreComputedOnce()
    {
        using var db = NewDb<BlogDbContext>();

        AnalyticsTables.For(db.Model).Should().BeSameAs(AnalyticsTables.For(db.Model));
    }

    [Fact]
    public void TheSql_FollowsTheModelsTableNames()
    {
        using var db = NewDb<RenamedTablesDbContext>();

        var tables = AnalyticsTables.For(db.Model);

        tables.InsertPageView.Should().Contain("\"PageViewsRenamed\"").And.NotContain("\"PageViews\"");
        tables.PurgePageViews.Should().Contain("\"PageViewsRenamed\"").And.NotContain("\"PageViews\"");
        tables.IncrementViewCount.Should().Contain("\"PostViewCountsRenamed\"").And.NotContain("\"PostViewCounts\"");
    }

    [Fact]
    public void TheDefaultModel_GivesTheShippedNames()
    {
        using var db = NewDb<BlogDbContext>();

        var tables = AnalyticsTables.For(db.Model);

        tables.PurgePageViews.Should().Contain("DELETE FROM \"PageViews\"").And.Contain("\"Day\" < {0}").And.Contain("LIMIT {1}");
        tables.InsertPageView.Should().StartWith("INSERT INTO \"PageViews\"").And.EndWith("DO NOTHING");
        tables.IncrementViewCount.Should().StartWith("INSERT INTO \"PostViewCounts\"");
    }

    internal static T NewDb<T>(string connectionString = "Host=localhost;Database=never_connected") where T : BlogDbContext =>
        (T)Activator.CreateInstance(typeof(T), new DbContextOptionsBuilder<BlogDbContext>()
            .UseNpgsql(connectionString).Options)!;

    /// <summary>A model that maps the analytics tables under other names.</summary>
    internal sealed class RenamedTablesDbContext(DbContextOptions<BlogDbContext> options) : BlogDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<PageView>().ToTable("PageViewsRenamed");
            modelBuilder.Entity<PostViewCount>().ToTable("PostViewCountsRenamed");
        }
    }
}
