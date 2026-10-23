using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Croft.Web.Data.Configurations;

public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.HasKey(x => x.SessionId);
        builder.Property(x => x.SessionId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Sid).HasMaxLength(256);
        builder.Property(x => x.Sub).HasMaxLength(256);
        builder.Property(x => x.AccessToken).IsRequired();
        // Back-channel logout finds sessions by IdP sid or sub, and the purge job by expiry.
        builder.HasIndex(x => x.Sid);
        builder.HasIndex(x => x.Sub);
        builder.HasIndex(x => x.ExpiresAt);
        builder.HasIndex(x => x.SessionExpiresAt);
    }
}
