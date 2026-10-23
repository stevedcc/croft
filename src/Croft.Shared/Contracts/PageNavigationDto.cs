namespace Croft.Shared.Contracts;

/// <summary>Both navigation lists in one read, so a reading page's shell needs one API call for
/// the sidebar's pages and the footer's: published pages by placement, NavigationOrder then title.</summary>
public record PageNavigationDto(IReadOnlyList<PageNavItemDto> Sidebar, IReadOnlyList<PageNavItemDto> Footer);
