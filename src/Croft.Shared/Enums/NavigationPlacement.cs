namespace Croft.Shared.Enums;

/// <summary>Where a page is linked from: nowhere, the sidebar, or the site footer. One placement
/// per page. Stored as a string in the database.</summary>
public enum NavigationPlacement
{
    None,
    Sidebar,
    Footer
}
