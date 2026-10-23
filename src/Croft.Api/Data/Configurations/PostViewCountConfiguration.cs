using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class PostViewCountConfiguration : IEntityTypeConfiguration<PostViewCount>
{
    public void Configure(EntityTypeBuilder<PostViewCount> builder)
    {
        builder.HasKey(x => x.PostId);
        builder.HasOne(x => x.Post)
            .WithOne(x => x.Views)
            .HasForeignKey<PostViewCount>(x => x.PostId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
