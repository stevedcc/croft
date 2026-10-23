using Croft.Shared.Contracts;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// TagsPage row accounting: the API's create endpoints are idempotent by slug (creating an
/// existing name returns that category/tag), so the row lists must merge by id — a create
/// racing the initial load used to add the same row twice and crash the circuit on the
/// duplicate @key.
/// </summary>
public class TagsPageTests : BlogBunitContext
{
    private static CategoryDto Category(string name = "Programming") =>
        new(Guid.CreateVersion7(), name, name.ToLowerInvariant(), null, 1);

    private static TagDto Tag(string name = "k8s", bool countsTowardsRelated = true) =>
        new(Guid.CreateVersion7(), name, name.ToLowerInvariant(), 1, countsTowardsRelated);

    /// <summary>Rows showing the name — it lives in the row's editable input value and the
    /// icon button's aria-label, so match on the markup rather than the text content.</summary>
    private static int RowsWith(IRenderedComponent<TagsPage> cut, string text) =>
        cut.FindAll("tr").Count(row => row.InnerHtml.Contains(text));

    private static async Task FillAndAddAsync(IRenderedComponent<TagsPage> cut, string label, string name)
    {
        var input = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == label);
        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync(name));
        input.Find(".mud-input-adornment button").Click(new MouseEventArgs());
    }

    [Fact]
    public async Task Listings_LoadOnePageAtATime_AndPageOnTheServer()
    {
        var fake = new FakeBlogApiClient
        {
            Tags = [.. Enumerable.Range(1, 15).Select(n => Tag($"Tag {n:00}"))],
        };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();

        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Should()
            .ContainSingle().Which.Should().Be((1, 10, (string?)null)));
        cut.FindAll("tr.admin-table__row").Should().HaveCount(10, "one page of rows renders, never the whole table");

        var pager = cut.FindComponent<MudBlazor.MudPagination>();
        await cut.InvokeAsync(() => pager.Instance.SelectedChanged.InvokeAsync(2));

        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Last().Should().Be((2, 10, (string?)null)));
        cut.FindAll("tr.admin-table__row").Should().HaveCount(5, "page 2 shows the remaining five");
    }

    [Fact]
    public async Task FilterBox_SendsQToTheServer_AndShowsOnlyTheMatches()
    {
        var k8s = Tag("k8s");
        var fake = new FakeBlogApiClient { Tags = [k8s, Tag("yaml")] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();
        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Should().NotBeEmpty());

        var field = cut.FindComponents<MudTextField<string>>().Single(f => f.Instance.Label == "Search tags");
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync("k8"));
        field.Find("input").KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await cut.WaitForAssertionAsync(() =>
        {
            fake.TagPageRequests.Last().Should().Be((1, 10, "k8"), "the filter runs on the server");
            RowsWith(cut, k8s.Name).Should().Be(1, "the matching row shows");
            RowsWith(cut, "yaml").Should().Be(0, "the non-matching row does not");
        });
    }

    [Fact]
    public async Task Rename_EditsOneRowAtATime_AndBlurSavesTheNewName()
    {
        var k8s = Tag("k8s");
        var fake = new FakeBlogApiClient { Tags = [k8s, Tag("yaml")] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();
        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Should().NotBeEmpty());

        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Rename k8s").Click(new MouseEventArgs());
        cut.FindComponents<MudTextField<string>>().Count(f => f.Instance.Label == "Name").Should().Be(1,
            "only the row being edited is an editor");

        var input = cut.Find("tr.admin-table__row input");
        input.Input(new ChangeEventArgs { Value = "kubernetes" });
        input.Blur(new FocusEventArgs());

        await cut.WaitForAssertionAsync(() => fake.Tags.Single(t => t.Id == k8s.Id).Name.Should().Be("kubernetes",
            "the rename reaches the API"));
        cut.FindComponents<MudTextField<string>>().Count(f => f.Instance.Label == "Name").Should().Be(0,
            "the editor closes once the row is saved");
        RowsWith(cut, "kubernetes").Should().Be(1, "the row shows the new name as text");
    }

    [Fact]
    public async Task AddCategory_WhenCreateReturnsAnExistingCategory_DoesNotDuplicateTheRow()
    {
        var existing = Category();
        var fake = new FakeBlogApiClient { Categories = [existing] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();

        // The API's create is idempotent by slug: the name already exists, so it returns that
        // category instead of making a new one.
        await FillAndAddAsync(cut, "New category", existing.Name);

        await cut.WaitForAssertionAsync(() => RowsWith(cut, existing.Name).Should().Be(1));
    }

    [Fact]
    public async Task CategoryLoad_LandingAfterTheCreate_DoesNotDuplicateTheRow()
    {
        var fake = new FakeBlogApiClient { CategoriesLoadGate = new TaskCompletionSource() };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();

        await FillAndAddAsync(cut, "New category", "Late Load");
        await cut.WaitForAssertionAsync(() => RowsWith(cut, "Late Load").Should().Be(1));

        // The initial load completes now and its list already contains the created category —
        // the other interleaving of the same race.
        fake.CategoriesLoadGate.SetResult();
        await cut.WaitForAssertionAsync(() => RowsWith(cut, "Late Load").Should().Be(1));
    }

    [Fact]
    public async Task AddTag_WhenCreateReturnsAnExistingTag_DoesNotDuplicateTheRow()
    {
        var existing = Tag();
        var fake = new FakeBlogApiClient { Tags = [existing] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();

        await FillAndAddAsync(cut, "New tag", existing.Name);

        await cut.WaitForAssertionAsync(() => RowsWith(cut, existing.Name).Should().Be(1));
    }

    [Fact]
    public async Task RelatedSwitch_ReflectsTheTagsValue_AndCarriesTheAccessibleName()
    {
        var on = Tag("alpha");
        var off = Tag("beta", countsTowardsRelated: false);
        var fake = new FakeBlogApiClient { Tags = [on, off] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();
        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Should().NotBeEmpty());

        var switches = cut.FindComponents<MudSwitch<bool>>();
        switches.Should().HaveCount(2, "every tag row has one switch");
        switches.Select(s => s.Find("input.mud-switch-input").GetAttribute("aria-checked"))
            .Should().Equal(["true", "false"], "the switch reflects the tag's saved value");

        foreach (var sw in switches)
        {
            var input = sw.Find("input.mud-switch-input");
            var nameSpan = sw.Find("span.mud-sr-only");
            input.GetAttribute("aria-labelledby").Should().Be(nameSpan.Id,
                "the switch's accessible name comes from the labelled span");
        }

        switches[0].Find("span.mud-sr-only").TextContent.Should().Be("Counts towards related posts: alpha");
        switches[1].Find("span.mud-sr-only").TextContent.Should().Be("Counts towards related posts: beta");
    }

    [Fact]
    public async Task RelatedSwitch_TogglingSavesAtOnce_WithTheCurrentNameAndTheNewValue()
    {
        var k8s = Tag("k8s");
        var fake = new FakeBlogApiClient { Tags = [k8s] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();
        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Should().NotBeEmpty());

        var sw = cut.FindComponent<MudSwitch<bool>>();
        await cut.InvokeAsync(() => sw.Instance.ValueChanged.InvokeAsync(false));

        await cut.WaitForAssertionAsync(() => fake.TagUpdateRequests.Should()
            .ContainSingle().Which.Should().Be((k8s.Id, new UpdateTagRequest("k8s", false)),
                "the toggle saves at once with the tag's current name and the new value"));
        await cut.WaitForAssertionAsync(() => cut.FindComponent<MudSwitch<bool>>()
            .Find("input.mud-switch-input").GetAttribute("aria-checked").Should().Be("false",
                "the row shows the save result like a rename does"));
    }

    [Fact]
    public async Task RelatedSwitch_FailedSave_PutsTheSwitchBack_AndShowsTheError()
    {
        var k8s = Tag("k8s");
        var fake = new FakeBlogApiClient { Tags = [k8s] };
        fake.UpdateFailure = new HttpRequestException("The API said no.");
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();
        await cut.WaitForAssertionAsync(() => fake.TagPageRequests.Should().NotBeEmpty());

        var sw = cut.FindComponent<MudSwitch<bool>>();
        await cut.InvokeAsync(() => sw.Instance.ValueChanged.InvokeAsync(false));

        await cut.WaitForAssertionAsync(() =>
        {
            cut.FindComponent<MudSwitch<bool>>().Find("input.mud-switch-input").GetAttribute("aria-checked")
                .Should().Be("true", "a failed save puts the switch back to its saved state");
            cut.Find(".admin-table__error").TextContent.Should().Be("Could not save the related setting.",
                "the error surfaces inline like the sibling save errors");
        });
    }

    [Theory]
    [InlineData("category")]
    [InlineData("tag")]
    public void DeleteTooltip_ExplainsADisabledDelete_NeverAnEnabledOne(string kind)
    {
        // Spec §6.5: the tooltip says why Delete is disabled — showing it on a deletable row
        // tells the user the opposite of the truth. bUnit has no hover, so the outcome is pinned
        // on the tooltip's state (AdminTests pins the hover behaviour itself).
        // Inputs renamed (brief-03i paging): the listings are name-ordered like the API, so the
        // used row's name must sort before the unused one for the positional assertions below.
        var used = kind == "category" ? Category("Busy Category") : null;
        var unused = kind == "category"
            ? new CategoryDto(Guid.CreateVersion7(), "Idle Category", "idle-category", null, 0)
            : null;
        var usedTag = kind == "tag" ? Tag("Busy Tag") : null;
        var unusedTag = kind == "tag"
            ? new TagDto(Guid.CreateVersion7(), "Idle Tag", "idle-tag", 0, true)
            : null;
        var fake = new FakeBlogApiClient
        {
            Categories = [.. new[] { used, unused }.OfType<CategoryDto>()],
            Tags = [.. new[] { usedTag, unusedTag }.OfType<TagDto>()],
        };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();

        var tooltip = $"Cannot delete while posts use this {kind}.";
        var tooltips = cut.FindComponents<MudTooltip>();
        tooltips.Should().HaveCount(2).And.OnlyContain(t => t.Instance.Text == tooltip);
        tooltips[0].Instance.Disabled.Should().BeFalse("the row whose Delete is disabled explains why");
        tooltips[1].Instance.Disabled.Should().BeTrue("an enabled Delete has nothing to explain");
    }

    [Fact]
    public async Task ChooseIcon_ApiFailure_ShowsTheError_AndKeepsTheIcon()
    {
        var category = Category();
        var fake = new FakeBlogApiClient { Categories = [category] };
        fake.UpdateFailure = new HttpRequestException("The API said no.");
        Services.AddSingleton<IBlogApiClient>(fake);
        var provider = Render<MudDialogProvider>();
        var cut = Render<TagsPage>();

        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == $"Icon for {category.Name}")
            .Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => provider.FindAll("button").Should().NotBeEmpty());
        provider.FindAll("button").Single(b => b.TextContent.Contains("Terrain")).Click(new MouseEventArgs());
        provider.FindAll("button").Single(b => b.TextContent == "Save").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".admin-table__error").TextContent.Should().Be("Could not save the icon.",
                "a failed icon save surfaces inline like the sibling save methods"));
    }

    private static AngleSharp.Dom.IElement NameInput(IRenderedComponent<TagsPage> cut, string kind) =>
        cut.FindAll("section").Single(s => s.QuerySelector("h2")!.TextContent == (kind == "category" ? "Categories" : "Tags"))
            .QuerySelector("tr.admin-table__row input")!;

    [Theory]
    [InlineData("category")]
    [InlineData("tag")]
    public async Task Rename_EnterSaves_AndEscapeCancelsWithoutReachingTheApi(string kind)
    {
        // Brief 13g item 2 (review I-5): a keyboard user commits with Enter and cancels with
        // Escape; blur still saves, so a cancel must also survive the blur that follows when the
        // field leaves the DOM.
        var category = Category("Programming");
        var tag = Tag("k8s");
        var fake = new FakeBlogApiClient { Categories = [category], Tags = [tag] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();
        var oldName = kind == "category" ? category.Name : tag.Name;
        string StoredName() => kind == "category"
            ? fake.Categories.Single().Name
            : fake.Tags.Single().Name;

        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == $"Rename {oldName}").Click(new MouseEventArgs());
        var input = NameInput(cut, kind);
        input.Input(new ChangeEventArgs { Value = "Discarded" });
        input.KeyDown(new KeyboardEventArgs { Key = "Escape" });

        cut.FindComponents<MudTextField<string>>().Count(f => f.Instance.Label == "Name").Should().Be(0,
            "Escape closes the editor");
        StoredName().Should().Be(oldName, "Escape saves nothing");
        cut.FindAll("button").Should().Contain(b => b.GetAttribute("aria-label") == $"Rename {oldName}",
            "the row shows its old name and the Rename button again");

        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == $"Rename {oldName}").Click(new MouseEventArgs());
        input = NameInput(cut, kind);
        input.Input(new ChangeEventArgs { Value = "Renamed" });
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        await cut.WaitForAssertionAsync(() => StoredName().Should().Be("Renamed", "Enter commits the rename"));
        cut.FindComponents<MudTextField<string>>().Count(f => f.Instance.Label == "Name").Should().Be(0,
            "Enter closes the editor");
    }

    [Theory]
    [InlineData("category")]
    [InlineData("tag")]
    public void DisabledDelete_ExplainsItselfInVisibleText_ThatTheButtonIsDescribedBy(string kind)
    {
        // Brief 13g item 2 (review I-5): a disabled button takes no focus and the tooltip needs a
        // hover, so the reason also stands as visible text, linked with aria-describedby.
        var fake = new FakeBlogApiClient { Categories = [Category("Busy")], Tags = [Tag("Busy")] };
        Services.AddSingleton<IBlogApiClient>(fake);
        var cut = Render<TagsPage>();

        var section = cut.FindAll("section").Single(s => s.QuerySelector("h2")!.TextContent == (kind == "category" ? "Categories" : "Tags"));
        var delete = section.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Delete");
        delete.HasAttribute("disabled").Should().BeTrue("a row in use cannot be deleted");
        var reasonId = delete.GetAttribute("aria-describedby");
        reasonId.Should().NotBeNullOrEmpty("the button points at its reason");
        var reason = section.QuerySelector($"#{reasonId}");
        reason.Should().NotBeNull("the id resolves to an element");
        reason!.TextContent.Trim().Should().Be("Used by 1 post, so it cannot be deleted.");
        reason.HasAttribute("hidden").Should().BeFalse("the reason is visible text");
    }
}
