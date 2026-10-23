namespace Croft.Shared.Contracts.Social;

/// <summary>What the editor's Discussion panel shows: every stored reply and each thread's status.</summary>
public sealed record PostDiscussionAdminDto(
    PostDiscussionDto Discussion,
    IReadOnlyList<DiscussionThreadStatusDto> Threads);
