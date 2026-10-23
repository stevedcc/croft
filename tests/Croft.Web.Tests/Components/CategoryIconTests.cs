using Croft.Web.Components.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor;

namespace Croft.Web.Tests.Components;

public class CategoryIconTests : BlogBunitContext
{
    public CategoryIconTests()
    {
        // Review Focus #4: unknown icon names must render the Folder fallback, never an
        // exception. Use the production catalogue so the whole chain is exercised.
        Services.RemoveAll<IIconCatalog>();
        Services.AddSingleton<IIconCatalog, IconCatalog>();
    }

    [Fact]
    public void CategoryIcon_NoSuchIcon_RendersSvgWithFolderPath()
    {
        var cut = Render<CategoryIcon>(p => p.Add(c => c.Name, "NoSuchIcon"));

        cut.Find("svg"); // fails the test if there is no <svg> root
        cut.Markup.Should().Contain(Icons.Material.Filled.Folder);
    }
}
