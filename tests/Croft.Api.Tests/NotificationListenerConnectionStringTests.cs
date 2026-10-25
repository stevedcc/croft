using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;
using Npgsql;

namespace Croft.Api.Tests;

/// <summary>
/// Brief 16b (CI on PR #67): the test fixture's LISTEN connection is never pooled. A pooled one
/// went back to the pool still listening, buffered a later test's NOTIFY, and handed it to the next
/// <see cref="NotificationListener"/>, so a "nothing was sent" assertion saw another test's write.
/// </summary>
public class NotificationListenerConnectionStringTests
{
    [Fact]
    public void TheListenerConnectionString_HasPoolingOff_AndKeepsTheDatabase()
    {
        var built = new NpgsqlConnectionStringBuilder(
            NotificationListener.BuildConnectionString("Host=localhost;Database=blog_test;Username=u;Password=p"));

        built.Pooling.Should().BeFalse("a listener returned to the pool keeps LISTENing and leaks notifications into the next test");
        built.Database.Should().Be("blog_test");
    }
}
