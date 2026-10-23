using System.Security.Claims;

namespace Croft.Api.Identity;

public static class UserExtensions
{
    /// <summary>
    /// The user's IdP subject. A token without <c>sub</c> is refused by the JWT bearer events
    /// (Program.cs), so no endpoint sees such a principal; this throws if one ever does, instead of
    /// collapsing every such user into one shared identity.
    /// </summary>
    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub") is { Length: > 0 } sub
            ? sub
            : throw new InvalidOperationException("The principal has no sub claim; the authentication must have refused it.");

    public static string GetUserName(this ClaimsPrincipal user) =>
        user.FindFirstValue("name")
        ?? user.FindFirstValue(ClaimTypes.Name)
        ?? "unknown";
}
