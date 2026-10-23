using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Croft.Web.Data;

/// <summary>
/// Lets dotnet-ef work without running the app's startup: at runtime the context is registered
/// only when ConnectionStrings:WebDb is set, and scaffolding runs with no configuration at all.
/// A ConnectionStrings__WebDb from the shell wins, so commands that act on a concrete database
/// ("database drop", "database update") reach the same one the app would; the placeholder below
/// is used for scaffolding, which diffs the model only and never connects. The placeholder
/// deliberately carries no credentials.
/// </summary>
public sealed class CroftWebDbContextFactory : IDesignTimeDbContextFactory<CroftWebDbContext>
{
    public CroftWebDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CroftWebDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__WebDb")
                ?? "Host=localhost;Database=croft_web_design_time")
            .Options);
}
