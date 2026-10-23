using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class SocialBlockConfiguration : IEntityTypeConfiguration<SocialBlock>
{
    public void Configure(EntityTypeBuilder<SocialBlock> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ExternalId).HasMaxLength(512);
        builder.Property(x => x.Label).HasMaxLength(512);
        builder.Property(x => x.Note).HasMaxLength(2000);
        builder.Property(x => x.CreatedBy).HasMaxLength(256);
        builder.HasIndex(x => new { x.Platform, x.Kind, x.ExternalId }).IsUnique();
    }
}
