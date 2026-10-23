using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class SocialReplyConfiguration : IEntityTypeConfiguration<SocialReply>
{
    public void Configure(EntityTypeBuilder<SocialReply> builder)
    {
        builder.HasKey(x => new { x.PostId, x.Platform, x.ExternalId });
        builder.HasOne(x => x.Post)
            .WithMany()
            .HasForeignKey(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.ExternalId).HasMaxLength(512);
        builder.Property(x => x.AvatarHash).HasMaxLength(64);

        // A block removes a reply or an account's replies on every post; the avatar sweep asks which hashes are still referenced.
        builder.HasIndex(x => new { x.Platform, x.ExternalId });
        builder.HasIndex(x => new { x.Platform, x.AuthorAccountId });
        builder.HasIndex(x => x.AvatarHash);
    }
}
