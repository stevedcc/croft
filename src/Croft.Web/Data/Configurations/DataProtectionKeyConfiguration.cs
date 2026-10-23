using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Web.Data.Configurations;

// WORKAROUND(W-028, ASP.NET Core): the column shape follows the framework's DataProtectionKey entity so its repository and the key import read the same rows
/// <summary>
/// The shared Data Protection key ring's table (Microsoft's DataProtectionKey entity): every Web
/// instance reads the same keys here, so a cookie any instance protected is readable by all. The
/// column shape follows the framework's entity — its repository reads and writes these rows, and
/// the "keys import" command inserts the same shape.
/// </summary>
public class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        builder.ToTable("DataProtectionKeys");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedOnAdd();
        builder.Property(x => x.FriendlyName).HasMaxLength(256);
        builder.Property(x => x.Xml).IsRequired();
    }
}
// END WORKAROUND(W-028)
