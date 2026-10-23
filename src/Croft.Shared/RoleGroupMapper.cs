using System.Security.Claims;

namespace Croft.Shared;

/// <summary>
/// Turns the OIDC `groups` and `role` claims into the blog's Admin/Editor role claims
/// (ClaimTypes.Role), shared by the API's and the Web app's token validation so the two cannot
/// drift apart. A `groups` value grants a role only when it equals the configured group name
/// (ordinal comparison); a `role` value passes through only when it is literally Admin or
/// Editor. Each role ends up on the identity at most once.
/// </summary>
public static class RoleGroupMapper
{
    public const string AdminRole = "Admin";
    public const string EditorRole = "Editor";

    public static void AddRoleClaims(ClaimsIdentity identity, RoleGroupSettings settings)
    {
        // Materialise first: FindAll is lazy and the loop below mutates the claims collection.
        string[] roles = [.. RolesFor(identity, settings)];
        foreach (var role in roles)
        {
            if (!identity.HasClaim(ClaimTypes.Role, role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }
    }

    private static IEnumerable<string> RolesFor(ClaimsIdentity identity, RoleGroupSettings settings)
    {
        foreach (var group in identity.FindAll("groups").Select(c => c.Value))
        {
            if (group == settings.Admin)
            {
                yield return AdminRole;
            }

            if (group == settings.Editor)
            {
                yield return EditorRole;
            }
        }

        foreach (var role in identity.FindAll("role").Select(c => c.Value))
        {
            if (role is AdminRole or EditorRole)
            {
                yield return role;
            }
        }
    }
}
