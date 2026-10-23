using System.Security.Claims;
using Croft.Shared;
using AwesomeAssertions;

namespace Croft.Web.Tests;

/// <summary>
/// The shared rule that turns the OIDC `groups`/`role` claims into the blog's Admin/Editor
/// roles: a `groups` value grants a role only when it equals the configured group name, and a
/// `role` value passes through only when it is literally Admin or Editor.
/// </summary>
public class RoleGroupMapperTests
{
    private static readonly RoleGroupSettings Configured = new() { Admin = "Blog Admins", Editor = "Blog Editors" };

    private static ClaimsIdentity IdentityWith(params Claim[] claims) => new(claims, "test");

    private static string[] RolesOf(ClaimsIdentity identity) =>
        [.. identity.FindAll(ClaimTypes.Role).Select(c => c.Value)];

    [Fact]
    public void ConfiguredGroupName_GrantsItsRole()
    {
        var identity = IdentityWith(new Claim("groups", "Blog Admins"), new Claim("groups", "Blog Editors"));

        RoleGroupMapper.AddRoleClaims(identity, Configured);

        RolesOf(identity).Should().BeEquivalentTo("Admin", "Editor");
    }

    [Fact]
    public void Unconfigured_DefaultsMapTheAdminAndEditorGroups()
    {
        var identity = IdentityWith(new Claim("groups", "Admin"), new Claim("groups", "Editor"));

        RoleGroupMapper.AddRoleClaims(identity, new RoleGroupSettings());

        RolesOf(identity).Should().BeEquivalentTo("Admin", "Editor");
    }

    [Fact]
    public void UnmappedGroup_GrantsNothing()
    {
        var identity = IdentityWith(new Claim("groups", "Admin"));

        RoleGroupMapper.AddRoleClaims(identity, Configured);

        RolesOf(identity).Should().BeEmpty("the group name Admin is not the configured Admin group");
    }

    [Fact]
    public void GroupNameMatch_IsCaseSensitive()
    {
        var identity = IdentityWith(new Claim("groups", "blog admins"));

        RoleGroupMapper.AddRoleClaims(identity, Configured);

        RolesOf(identity).Should().BeEmpty("group names are matched ordinally");
    }

    [Fact]
    public void RoleClaimAdmin_PassesThrough()
    {
        var identity = IdentityWith(new Claim("role", "Admin"), new Claim("role", "Editor"));

        RoleGroupMapper.AddRoleClaims(identity, Configured);

        RolesOf(identity).Should().BeEquivalentTo("Admin", "Editor");
    }

    [Fact]
    public void OtherRoleClaimValue_GrantsNothing()
    {
        var identity = IdentityWith(new Claim("role", "SomethingElse"));

        RoleGroupMapper.AddRoleClaims(identity, Configured);

        RolesOf(identity).Should().BeEmpty("only the blog's own roles pass through");
    }

    [Fact]
    public void BothClaimsSayTheSame_AddsASingleRoleClaim()
    {
        var identity = IdentityWith(new Claim("groups", "Blog Admins"), new Claim("role", "Admin"));

        RoleGroupMapper.AddRoleClaims(identity, Configured);

        RolesOf(identity).Should().Equal("Admin");
    }
}
