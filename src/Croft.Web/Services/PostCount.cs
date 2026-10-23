namespace Croft.Web.Services;

/// <summary>How a number of posts is written: "1 post", "12 posts". One place, so a count never reads
/// "1 posts" (the list headers) and the sidebar's hidden unit and the admin's reasons agree.</summary>
public static class PostCount
{
    /// <summary>"post" for one, "posts" otherwise.</summary>
    public static string Unit(int count) => count == 1 ? "post" : "posts";

    /// <summary>"1 post", "12 posts".</summary>
    public static string Text(int count) => $"{count} {Unit(count)}";
}
