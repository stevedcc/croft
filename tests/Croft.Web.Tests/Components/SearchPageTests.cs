using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Pages;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The search page's field (spec §5.3, ruling R15): a plain search input styled with CSS
/// classes — no MudBlazor input and no style attribute anywhere, because the public policy bans
/// them (style-src-attr 'none') and an attribute-value hash would need 'unsafe-hashes'. What the
/// user sees and does is unchanged: the visible "Search" label, the Search button, the accessible
/// name, no search-as-you-type, and a search that runs on the button or Enter — since brief 09f
/// as a plain GET form (the page is static SSR and has no circuit to run a handler).
/// </summary>
public class SearchPageTests : BlogBunitContext
{
    private static PostSummaryDto Summary(string slug, string title) =>
        new(Guid.CreateVersion7(), slug, title, null, null, "A summary.", 2, PostStatus.Published,
            new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc),
            "An Author", null, [], null, 4, 1);

    private FakeBlogApiClient Setup()
    {
        var api = new FakeBlogApiClient { Posts = [Summary("hills", "Hills Notes")] };
        Services.AddSingleton<IBlogApiClient>(api);
        return api;
    }

    [Fact]
    public void SearchPage_Field_IsAPlainSearchInput_WithoutAnyStyleAttribute()
    {
        Setup();
        var cut = Render<Search>();

        cut.Markup.Should().NotContain("style=\"",
            "the public pages write no style attribute (style-src-attr 'none'), and an attribute hash would need 'unsafe-hashes'");
        var input = cut.Find("input[type='search']"); // also: the plain input, not MudBlazor's
        input.HasAttribute("role").Should().BeFalse(
            "type=search alone exposes role searchbox; a role=textbox on it would demote that (brief 13g item 3, review I-3)");
        var label = cut.Find("label");
        label.TextContent.Should().Be("Search", "the visible label is what names the field");
        label.GetAttribute("for").Should().Be(input.Id, "the label names this input");
        cut.FindAll("button[aria-label='Search']").Should().HaveCount(1, "the search action keeps its accessible name");
        cut.Find("button[aria-label='Search']").HasAttribute("tabindex").Should().BeFalse(
            "the search button is a tab stop: a keyboard user who tabs past the field to press Search must find it (brief 13g item 3, review I-3)");
    }

    [Fact]
    public void SearchPage_Field_CarriesNoMudBlazorClass_AndNoStyleAttribute()
    {
        // Brief 09i: the reading pages load no MudBlazor stylesheet, so the field is styled by
        // the page's own classes; a mud- class would be dead markup (and a lie about the CSS).
        Setup();
        var cut = Render<Search>();

        cut.Markup.Should().NotContain("mud-", "no MudBlazor class, id or variable name belongs in the static field");
        cut.FindAll("[style]").Should().BeEmpty("the public policy bans style attributes");
        cut.FindAll("svg").Should().ContainSingle("the search button's icon is one inline SVG, not a MudIcon")
            .Which.GetAttribute("aria-hidden").Should().Be("true", "the button's aria-label names it; the icon is decoration");
    }

    [Fact]
    public void SearchPage_Field_IsInsideAGetForm_ThatSearchesWithQ()
    {
        // Brief 09f item 3: the page is static SSR (no circuit), so the search is a plain GET form:
        // Enter and the button both submit it to /search?q=..., which the server reads.
        Setup();
        var cut = Render<Search>();

        var form = cut.Find("form");
        form.GetAttribute("method").Should().Be("get");
        form.GetAttribute("action").Should().Be("/search");
        form.GetAttribute("role").Should().Be("search");
        form.HasAttribute("data-enhance").Should().BeTrue("Blazor submits a GET form as an enhanced navigation only with data-enhance");
        form.QuerySelector("input[type='search']")!.GetAttribute("name").Should().Be("q",
            "the field's name is the query parameter the server reads");
        cut.Find("button[aria-label='Search']").GetAttribute("type").Should().Be("submit",
            "the adornment button submits the form");
    }

    [Fact]
    public void SearchPage_WithAQuery_KeepsTheTermInTheBox()
    {
        var api = Setup();
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/search?q=hills%20%26%20vales");

        var cut = Render<Search>();

        cut.Find("input[name='q']").GetAttribute("value").Should().Be("hills & vales",
            "after a search the box still shows the term");
        api.SearchRequests.Should().ContainSingle().Which.Should().Be("hills & vales");
    }

    [Theory]
    [InlineData(1, "1 result for 'hills'")]
    [InlineData(2, "2 results for 'hills'")]
    public void SearchPage_Header_UsesTheSingularForOneResult(int count, string expected)
    {
        var api = new FakeBlogApiClient
        {
            Posts = [.. Enumerable.Range(1, count).Select(n => Summary($"hills-{n}", $"Hills Notes {n}"))],
        };
        Services.AddSingleton<IBlogApiClient>(api);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/search?q=hills");

        var cut = Render<Search>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(expected));
    }

    [Fact]
    public void SearchPage_Field_CarriesNoImitationMudBlazorAria_AndTheDecorationsAreHidden()
    {
        // Brief 13g item 3 (review I-3): aria-invalid, aria-required and maxlength were MudBlazor
        // markup copied onto a static page where they do nothing; the icon is already aria-hidden
        // (a role on it contradicts that); the decorative fieldset is a second, empty group named
        // "Search" in the accessibility tree unless it is hidden.
        Setup();
        var cut = Render<Search>();

        var input = cut.Find("input[type='search']");
        input.HasAttribute("aria-invalid").Should().BeFalse();
        input.HasAttribute("aria-required").Should().BeFalse();
        input.HasAttribute("maxlength").Should().BeFalse();
        cut.Find("svg.search-field__icon").HasAttribute("role").Should().BeFalse("the icon is hidden, not an image");
        cut.Find("svg.search-field__icon").GetAttribute("aria-hidden").Should().Be("true");
        cut.Find("fieldset.search-field__border").GetAttribute("aria-hidden").Should().Be("true");
    }
}
