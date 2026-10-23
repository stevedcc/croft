using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.FileName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.OriginalName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.ContentType).HasMaxLength(256).IsRequired();
        builder.Property(x => x.AltText).HasMaxLength(300);
        builder.Property(x => x.FilePath).HasMaxLength(1024).IsRequired();
        builder.Property(x => x.UploadedBy).HasMaxLength(256).IsRequired();
    }
}
