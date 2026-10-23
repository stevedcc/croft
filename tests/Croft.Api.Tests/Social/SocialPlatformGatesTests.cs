using AwesomeAssertions;
using Croft.Api.Social;
using Croft.Shared.Contracts.Social;

namespace Croft.Api.Tests.Social;

/// <summary>Brief 12a item 3: one request at a time per platform; the platforms do not wait for each other.</summary>
public class SocialPlatformGatesTests
{
    [Fact]
    public async Task TwoRequestsToOnePlatform_NeverOverlap()
    {
        var gates = new SocialPlatformGates();
        var inFlight = 0;
        var maxInFlight = 0;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = gates.RunAsync(SocialPlatform.Mastodon, async () =>
        {
            maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
            firstEntered.SetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref inFlight);
            return 1;
        }, CancellationToken.None);
        await firstEntered.Task;

        var second = gates.RunAsync(SocialPlatform.Mastodon, () =>
        {
            maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
            Interlocked.Decrement(ref inFlight);
            return Task.FromResult(2);
        }, CancellationToken.None);

        second.IsCompleted.Should().BeFalse("the second request waits for the first");
        releaseFirst.SetResult();
        (await first).Should().Be(1);
        (await second).Should().Be(2);
        maxInFlight.Should().Be(1);
    }

    [Fact]
    public async Task TheTwoPlatforms_DoNotWaitForEachOther()
    {
        var gates = new SocialPlatformGates();
        var releaseMastodon = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mastodonEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mastodon = gates.RunAsync(SocialPlatform.Mastodon, async () =>
        {
            mastodonEntered.SetResult();
            await releaseMastodon.Task;
            return 1;
        }, CancellationToken.None);
        await mastodonEntered.Task;

        var bluesky = await gates.RunAsync(SocialPlatform.Bluesky, () => Task.FromResult(2), CancellationToken.None);

        bluesky.Should().Be(2, "Bluesky ran while Mastodon was still busy");
        releaseMastodon.SetResult();
        await mastodon;
    }

    [Fact]
    public async Task AFailingRequest_ReleasesTheGate()
    {
        var gates = new SocialPlatformGates();

        var act = () => gates.RunAsync<int>(SocialPlatform.Bluesky, () => throw new InvalidOperationException("boom"), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await gates.RunAsync(SocialPlatform.Bluesky, () => Task.FromResult(7), CancellationToken.None)).Should().Be(7);
    }

    [Fact]
    public async Task AWaiterThatIsCancelled_LeavesTheGateFree()
    {
        var gates = new SocialPlatformGates();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gates.RunAsync(SocialPlatform.Mastodon, async () =>
        {
            entered.SetResult();
            await release.Task;
            return 1;
        }, CancellationToken.None);
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var waiter = gates.RunAsync(SocialPlatform.Mastodon, () => Task.FromResult(2), cancel.Token);
        await cancel.CancelAsync();

        var act = () => waiter;
        await act.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        await holder;
        (await gates.RunAsync(SocialPlatform.Mastodon, () => Task.FromResult(3), CancellationToken.None)).Should().Be(3);
    }
}
