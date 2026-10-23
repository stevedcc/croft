using AwesomeAssertions;
using Croft.Web.Options;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Croft.Web.Tests.OutputCache;

public class OutputCacheSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Defaults_AreAMinuteForPages_FiveForTheFeed_32MegabytesAnd1024KilobytesPerPage()
    {
        var settings = OutputCacheSettings.Read(Config());

        settings.Duration.Should().Be(TimeSpan.FromMinutes(1));
        settings.FeedDuration.Should().Be(TimeSpan.FromMinutes(5));
        settings.SizeLimitMegabytes.Should().Be(32);
        settings.MaximumBodyKilobytes.Should().Be(1024);
        settings.MediaMaximumBodyKilobytes.Should().Be(512, "brief 14c item 6: an image above half a megabyte is streamed, not kept");
    }

    [Fact]
    public void TheMediaBodyLimit_IsRead()
    {
        OutputCacheSettings.Read(Config(("Croft:Web:OutputCache:MediaMaximumBodyKilobytes", "128"))).MediaMaximumBodyKilobytes.Should().Be(128);
    }

    [Fact]
    public void ConfiguredValues_AreRead()
    {
        var settings = OutputCacheSettings.Read(Config(
            ("Croft:Web:OutputCache:Duration", "00:00:30"),
            ("Croft:Web:OutputCache:FeedDuration", "00:10:00"),
            ("Croft:Web:OutputCache:SizeLimitMegabytes", "8"),
            ("Croft:Web:OutputCache:MaximumBodyKilobytes", "64")));

        settings.Should().Be(new OutputCacheSettings(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), 8, 64));
    }

    [Theory]
    [InlineData("Duration", "soon")]
    [InlineData("Duration", "-00:00:01")]
    [InlineData("FeedDuration", "x")]
    [InlineData("SizeLimitMegabytes", "-1")]
    [InlineData("SizeLimitMegabytes", "lots")]
    [InlineData("MaximumBodyKilobytes", "-5")]
    [InlineData("MediaMaximumBodyKilobytes", "0")]
    [InlineData("MediaMaximumBodyKilobytes", "big")]
    public void AnInvalidValue_FailsNamingTheSetting(string name, string value)
    {
        var read = () => OutputCacheSettings.Read(Config(($"Croft:Web:OutputCache:{name}", value)));

        read.Should().Throw<InvalidOperationException>().WithMessage($"*Croft:Web:OutputCache:{name}*");
    }

    [Fact]
    public void ADurationOfZero_IsValid_AndMeansPageCachingIsOff()
    {
        var settings = OutputCacheSettings.Read(Config(("Croft:Web:OutputCache:Duration", "0")));

        settings.Duration.Should().Be(TimeSpan.Zero);
        settings.PagesCached.Should().BeFalse();
    }

    [Fact]
    public void TheHostsCache_IsBoundedBy_TheSizeAndBodySettings()
    {
        using var defaults = CacheHost.Create();
        using var small = CacheHost.Create(new()
        {
            ["Croft:Web:OutputCache:SizeLimitMegabytes"] = "2",
            ["Croft:Web:OutputCache:MaximumBodyKilobytes"] = "16",
        });

        var byDefault = defaults.Services.GetRequiredService<IOptions<OutputCacheOptions>>().Value;
        var configured = small.Services.GetRequiredService<IOptions<OutputCacheOptions>>().Value;

        (byDefault.SizeLimit, byDefault.MaximumBodySize).Should().Be((32L * 1024 * 1024, 1024L * 1024));
        (configured.SizeLimit, configured.MaximumBodySize).Should().Be((2L * 1024 * 1024, 16L * 1024));
    }
}
