using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class SocialThreadConfiguration : IEntityTypeConfiguration<SocialThread>
{
    public void Configure(EntityTypeBuilder<SocialThread> builder)
    {
        builder.HasKey(x => new { x.PostId, x.Platform });
        builder.HasOne(x => x.Post)
            .WithMany()
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.LastError).HasMaxLength(500);
        builder.Property(x => x.SnapshotHash).HasMaxLength(64);

        // The fetcher claims the threads that are due.
        builder.HasIndex(x => x.NextDueAt);
    }
}
