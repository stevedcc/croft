using Croft.Shared.Contracts.Social;

namespace Croft.Api.Social;

/// <summary>
/// One request at a time per platform (brief 12a): a platform's requests queue behind each other, awaited; the two platforms do not
/// wait for one another. A singleton, so every fetch in the process shares the gates.
/// </summary>
public sealed class SocialPlatformGates
{
    private readonly Dictionary<SocialPlatform, SemaphoreSlim> _gates = Enum.GetValues<SocialPlatform>().ToDictionary(p => p, _ => new SemaphoreSlim(1, 1));

    public async Task<T> RunAsync<T>(SocialPlatform platform, Func<Task<T>> work, CancellationToken ct)
    {
        var gate = _gates[platform];
        await gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
        }
    }
}
