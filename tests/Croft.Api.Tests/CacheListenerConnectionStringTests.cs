using AwesomeAssertions;
using Croft.Api.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 13h item 3: the cache listener's LISTEN connection is never pooled. A connector returned
/// to the pool (after DISCARD ALL) and taken again by a new listener got no reply to its first
/// command for the whole command timeout under load (brief 13f's evidence), and a reconnect after a
/// failure must not pick up a pooled connector of the dead connection's pool.
/// </summary>
public class CacheListenerConnectionStringTests
{
    [Fact]
    public void TheListenerConnectionString_HasPoolingOff_AndKeepsItsOwnSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BlogDb"] = "Host=localhost;Database=blog;Username=u;Password=p",
            })
            .Build();

        var built = new NpgsqlConnectionStringBuilder(CacheListenerService.BuildConnectionString(configuration));

        built.Pooling.Should().BeFalse("a dedicated long-lived LISTEN connection gains nothing from a pool and must not reuse a pooled connector");
        built.ApplicationName.Should().Be("croft-cache-listener");
        built.KeepAlive.Should().Be(15);
        built.Timeout.Should().Be(5);
    }
}
