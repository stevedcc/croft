using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class FeedbackConfiguration : IEntityTypeConfiguration<Feedback>
{
    public void Configure(EntityTypeBuilder<Feedback> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AuthorId).HasMaxLength(256).IsRequired();
        builder.Property(x => x.AuthorName).HasMaxLength(256).IsRequired();
        builder.Property(x => x.QuotedText).IsRequired();
        builder.Property(x => x.Comment).IsRequired();
    }
}
