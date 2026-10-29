using AwesomeAssertions;
using Croft.Api.Tests.Fixtures;

namespace Croft.Api.Tests;

/// <summary>
/// The CROFT_TEST_POSTGRES choice: a set, non-blank value is the server the suite uses
/// instead of a Testcontainer (Chargehand's verify lane has no container runtime and sets
/// it). Null, empty and whitespace mean "no given server" and fall back to the container.
/// </summary>
public class TestPostgresTests
{
    [Fact]
    public void Null_MeansNoGivenServer() =>
        TestPostgres.GivenServer(null).Should().BeNull();

    [Fact]
    public void Empty_MeansNoGivenServer() =>
        TestPostgres.GivenServer("").Should().BeNull();

    [Fact]
    public void Whitespace_MeansNoGivenServer() =>
        TestPostgres.GivenServer("  ").Should().BeNull();

    [Theory]
    [InlineData("Host=localhost;Port=5432;Database=postgres;Username=blog;Password=blog")]
    [InlineData("  Host=localhost;Port=5432;Database=postgres;Username=blog;Password=blog  ")]
    public void AConnectionString_ComesBackUnchangedApartFromTrimming(string value) =>
        TestPostgres.GivenServer(value).Should()
            .Be("Host=localhost;Port=5432;Database=postgres;Username=blog;Password=blog");
}
