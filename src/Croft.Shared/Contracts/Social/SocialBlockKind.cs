namespace Croft.Shared.Contracts.Social;

/// <summary>What a block hides: one reply, or every reply of one account. The numeric values are stored in the database.</summary>
public enum SocialBlockKind
{
    Reply = 1,
    Account = 2,
}
