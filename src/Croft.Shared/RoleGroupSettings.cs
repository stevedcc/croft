namespace Croft.Shared;

/// <summary>
/// Which external group names grant each blog role, bound from the "Oidc:RoleGroups"
/// configuration section. The role names inside the apps stay Admin/Editor; only the group
/// names that grant them are configurable. The defaults keep local development (the mock OIDC
/// server sends the groups "Admin" and "Editor") behaving as before.
/// </summary>
public class RoleGroupSettings
{
    public const string SectionName = "Oidc:RoleGroups";

    public string Admin { get; set; } = "Admin";
    public string Editor { get; set; } = "Editor";
}
