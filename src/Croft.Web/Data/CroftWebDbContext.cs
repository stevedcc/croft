using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Croft.Web.Data;

/// <summary>
/// The Web app's optional database (ConnectionStrings:WebDb): shared state — token sessions and
/// Data Protection keys — lives here so any Web instance can serve any signed-in user. Without
/// the connection string the app registers no context at all and keeps its single-instance
/// behaviour (in-memory token store, keys on the file system).
/// </summary>
public class CroftWebDbContext(DbContextOptions<CroftWebDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<UserSession> UserSessions => Set<UserSession>();

    /// <summary>The Data Protection key ring shared by every Web instance (Microsoft's entity).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CroftWebDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
