using Croft.Api.Models;
using Croft.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class PageViewConfiguration : IEntityTypeConfiguration<PageView>
{
    public void Configure(EntityTypeBuilder<PageView> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IpHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Referrer).HasMaxLength(AnalyticsService.MaxReferrerLength);
        builder.Property(x => x.UserAgent).HasMaxLength(AnalyticsService.MaxUserAgentLength);
        builder.Property(x => x.Day).HasColumnType("date");
        // One view per post, visitor and day; the insert itself is the dedupe check. A visit that crosses midnight UTC
        // counts once too (AnalyticsService.MidnightGrace: the first 30 minutes of a day look back one day).
        builder.HasIndex(x => new { x.PostId, x.IpHash, x.Day }).IsUnique();
        builder.HasIndex(x => new { x.PostId, x.Timestamp });
    }
}
