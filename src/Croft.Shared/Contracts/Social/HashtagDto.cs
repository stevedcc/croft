namespace Croft.Shared.Contracts.Social;

/// <summary>Which networks a hashtag is added to in the default announcement.</summary>
public enum HashtagNetworks
{
    Both,
    MastodonOnly,
    BlueskyOnly,
}

/// <summary>A hashtag of a category or tag. <c>Name</c> is stored without the leading <c>#</c>.</summary>
public record HashtagDto(string Name, HashtagNetworks Networks);
