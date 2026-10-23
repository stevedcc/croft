namespace Croft.Web.Components.Layout;

/// <summary>
/// The shell the error page renders in: the same markup as <see cref="MainLayout"/> without the
/// API calls for the sidebar's categories and navigation. The error page is what a reader sees
/// when the API is down, so it must not depend on the API (brief 13c item 1).
/// </summary>
public sealed class ErrorLayout : MainLayout
{
    protected override bool LoadsApiData => false;
}
