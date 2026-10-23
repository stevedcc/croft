using Croft.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Api.Data.Configurations;

public class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.Slug).IsUnique();
        builder.Property(x => x.Name).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Slug).HasMaxLength(128).IsRequired();
        // WORKAROUND(W-029, EF Core): EF omits a property equal to its CLR default from the INSERT when a database default exists, so the sentinel is true
        // The sentinel is true, not the bool default false: with false as sentinel an explicit
        // false would read as "not set", be omitted from the INSERT and lose to the database
        // default true. With true as sentinel the database default fills the count case and an
        // explicit false is stored as false.
        builder.Property(x => x.CountsTowardsRelated).HasDefaultValue(true).HasSentinel(true);
        // END WORKAROUND(W-029)
    }
}
