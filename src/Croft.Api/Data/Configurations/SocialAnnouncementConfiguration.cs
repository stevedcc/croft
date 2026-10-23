using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class SocialAnnouncementConfiguration : IEntityTypeConfiguration<SocialAnnouncement>
{
    public void Configure(EntityTypeBuilder<SocialAnnouncement> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasOne(x => x.Post)
            .WithMany()
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.LastError).HasMaxLength(500);

        // One row per post and network: queueing twice cannot send twice.
        builder.HasIndex(x => new { x.PostId, x.Platform }).IsUnique();

        // The sender claims the rows that are due.
        builder.HasIndex(x => x.NextAttemptAt);
    }
}
