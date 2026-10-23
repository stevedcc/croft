using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class PageViewConfiguration : IEntityTypeConfiguration<PageView>
{
    public void Configure(EntityTypeBuilder<PageView> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.IpHash).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Day).HasColumnType("date");
        // One view per post, visitor and day; the insert itself is the dedupe check.
        builder.HasIndex(x => new { x.PostId, x.IpHash, x.Day }).IsUnique();
        builder.HasIndex(x => new { x.PostId, x.Timestamp });
    }
}
