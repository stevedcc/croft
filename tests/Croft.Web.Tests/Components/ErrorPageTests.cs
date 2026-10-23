using Bunit;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The error page (the exception handler's endpoint): restyled with the shell's typography like
/// NotFoundContent, with the request-id display and the HttpContext usage kept (nothing of the
/// stock template's red-heading markup or Development-mode copy remains).
/// </summary>
public class ErrorPageTests : BlogBunitContext
{
    [Fact]
    public void ErrorPage_ShellLanguage_WithoutTheStockTemplateCopy()
    {
        var cut = Render<Croft.Web.Components.Pages.Error>();

        // Brief 13g item 5 (review M-6): the stock template's Development-mode paragraphs are developer
        // documentation shown to readers; they are stripped. The apology and the request id stay.
        cut.Markup.Should().NotContain("Development environment");
        cut.Markup.Should().Contain("An error occurred while processing your request.");

        // The restyle outcome: the stock template's red heading classes are gone and the page is
        // plain markup (a static document since brief 13a loads no MudBlazor).
        cut.Markup.Should().NotContain("text-danger");
        cut.Markup.Should().NotContain("mud-");
        cut.Find(".error__title").TextContent.Should().Be("Something went wrong");
    }
}
