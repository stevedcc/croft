using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class SocialAvatarConfiguration : IEntityTypeConfiguration<SocialAvatar>
{
    public void Configure(EntityTypeBuilder<SocialAvatar> builder)
    {
        builder.HasKey(x => x.Hash);
        builder.Property(x => x.Hash).HasMaxLength(64);
        builder.HasIndex(x => x.SourceUrl);
    }
}
