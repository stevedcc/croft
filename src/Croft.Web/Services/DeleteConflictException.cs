namespace Croft.Web.Services;

/// <summary>
/// The API refuses a delete that would break something still in use (409 ProblemDetails): a tag
/// or category that posts still use, or a banner image a post still shows. The message is shown
/// to the user verbatim.
/// </summary>
public sealed class DeleteConflictException(string detail) : Exception(detail);
