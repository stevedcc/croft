namespace Croft.Web;

/// <summary>
/// A site-files folder the app cannot use: a disallowed file type, an oversized file or a
/// shadowing path. Web fails at startup with a message naming the file and the rule, like
/// <see cref="BrandingException"/> for the branding folder.
/// </summary>
public sealed class SiteFilesException(string message) : Exception(message);