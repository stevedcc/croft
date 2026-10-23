namespace Croft.Shared;

/// <summary>
/// The one content limit both sides share (a long technical post is about 60K characters, so
/// 200K leaves room): the API rejects a create or update whose markdown content is longer, and
/// the editor counts toward it and disables saving over it. The rule lives here so the API's
/// validation and the editor's counter cannot drift apart. The database column stays unlimited
/// text, and the editor never truncates — a text over the limit is kept in the textarea.
/// </summary>
public static class PostContent
{
    /// <summary>The longest post content, in characters.</summary>
    public const int MaxLength = 200_000;
}
