using Croft.Web.Services;
using AwesomeAssertions;

namespace Croft.Web.Tests;

public class InMemoryTokenStoreTests
{
    private static readonly TokenEntry Entry =
        new("at-1", "rt-1", new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task SetAndGet_RoundTripsEntry()
    {
        var store = new InMemoryTokenStore();

        await store.SetAsync("s1", Entry);

        (await store.GetAsync("s1")).Should().Be(Entry);
    }

    [Fact]
    public async Task Get_UnknownSession_ReturnsNull()
    {
        var store = new InMemoryTokenStore();

        (await store.GetAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task Remove_DeletesEntry()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", Entry);

        await store.RemoveAsync("s1");

        (await store.GetAsync("s1")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveBySid_RemovesOnlyEntriesWithThatSid()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", Entry with { Sid = "idp-sid-1", Sub = "admin-id" });
        await store.SetAsync("s2", Entry with { Sid = "idp-sid-2", Sub = "admin-id" });
        await store.SetAsync("s3", Entry with { Sid = "idp-sid-1", Sub = "editor-id" });

        (await store.RemoveBySidAsync("idp-sid-1")).Should().Be(2);

        (await store.GetAsync("s1")).Should().BeNull();
        (await store.GetAsync("s3")).Should().BeNull();
        (await store.GetAsync("s2")).Should().NotBeNull("only the sessions of the logged-out sid go");
    }

    [Fact]
    public async Task RemoveBySid_UnknownSid_RemovesNothing()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", Entry with { Sid = "idp-sid-1" });

        (await store.RemoveBySidAsync("nope")).Should().Be(0);

        (await store.GetAsync("s1")).Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveBySub_RemovesEverySessionOfThatUser()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", Entry with { Sid = "idp-sid-1", Sub = "admin-id" });
        await store.SetAsync("s2", Entry with { Sid = "idp-sid-2", Sub = "admin-id" });
        await store.SetAsync("s3", Entry with { Sid = "idp-sid-3", Sub = "editor-id" });

        (await store.RemoveBySubAsync("admin-id")).Should().Be(2);

        (await store.GetAsync("s1")).Should().BeNull();
        (await store.GetAsync("s2")).Should().BeNull();
        (await store.GetAsync("s3")).Should().NotBeNull("other users' sessions are untouched");
    }

    [Fact]
    public async Task RemoveBySub_UnknownSub_RemovesNothing()
    {
        var store = new InMemoryTokenStore();
        await store.SetAsync("s1", Entry with { Sub = "admin-id" });

        (await store.RemoveBySubAsync("nope")).Should().Be(0);

        (await store.GetAsync("s1")).Should().NotBeNull();
    }
}
