using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Croft.Api.Models;

namespace Croft.Api.Social;

/// <summary>
/// The hash of a thread's stored replies (brief 12a): replies sorted by external id, every stored field of each (length-prefixed, so
/// no field can bleed into the next), except <c>FetchedAt</c>, which changes with every fetch (times to the microsecond), and the post, which is the thread's own.
/// A fetch that yields the hash already stored changed nothing visible and invalidates nothing.
/// </summary>
public static class SocialSnapshot
{
    public static string Hash(IEnumerable<SocialReply> replies)
    {
        var text = new StringBuilder();
        foreach (var reply in replies.OrderBy(r => r.ExternalId, StringComparer.Ordinal))
        {
            Field(text, reply.ExternalId);
            Field(text, reply.ParentExternalId);
            Field(text, reply.AuthorHandle);
            Field(text, reply.AuthorDisplayName);
            Field(text, reply.AuthorUrl);
            Field(text, reply.AuthorAccountId);
            Field(text, reply.AvatarHash);
            // Microseconds: that is all Postgres keeps, so a row read back hashes as the reply that was stored.
            Field(text, (DateTime.SpecifyKind(reply.CreatedAt, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerMicrosecond).ToString(CultureInfo.InvariantCulture));
            Field(text, reply.Permalink);
            Field(text, reply.ContentHtml);
            Field(text, reply.ContentWarning);
            Field(text, reply.HasMedia ? "1" : "0");
            text.Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static void Field(StringBuilder text, string? value)
    {
        if (value is null)
            text.Append('~');
        else
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        text.Append('\u001F');
    }
}
