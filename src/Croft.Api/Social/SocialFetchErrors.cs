namespace Croft.Api.Social;

/// <summary>The failures of asking a platform that are expected (the network, a slow answer, a huge one, a surprising one), as the short text a thread records.</summary>
internal static class SocialFetchErrors
{
    public static bool IsExpected(Exception exception, CancellationToken ct) =>
        exception is HttpRequestException or SocialResponseTooLargeException || (exception is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>A kind and no detail: an exception message can carry an address or a name, which a stored error must not.</summary>
    public static SocialFetchOutcome.Failed From(string platform, Exception exception) => new(Reason(platform, exception));

    /// <summary>The same short text as a string (the announcers' retry reasons use it too).</summary>
    public static string Reason(string platform, Exception exception) => exception switch
    {
        SocialResponseTooLargeException => $"{platform}'s answer was too large",
        OperationCanceledException => $"{platform} timed out",
        _ => $"{platform} could not be reached (network error)",
    };
}
