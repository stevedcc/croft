namespace Croft.Web.Services;

internal static partial class DiscussionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "The discussion of post {Slug} could not be read, the post is shown without it: {ExceptionType}")]
    public static partial void DiscussionUnavailable(ILogger logger, string slug, string exceptionType);
}
