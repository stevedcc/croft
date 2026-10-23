using System.Text.RegularExpressions;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace Croft.Web.Tests.Components;

/// <summary>
/// PostEditorForm (spec §6.2/§7.4): the left column — the fields, the tag chips, the banner and
/// markdown editors — and the actions bar. "Schedule" is enabled only for a UTC moment in the
/// future (relative to the injected TimeProvider); the page decides what each action does.
/// </summary>
public class PostEditorFormTests : BlogBunitContext
{
    private static PostEditModel Model(DateTime? date = null, TimeSpan? time = null) => new()
    {
        Title = "A title",
        MarkdownContent = "Some body.",
        PublishedAtDate = date,
        PublishedAtTime = time,
    };

    private static FakeTimeProvider ClockAt(int hour) =>
        new(new DateTimeOffset(2026, 10, 6, hour, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Category_PickerToggleButton_HasItsOwnAccessibleName()
    {
        // The autocomplete's dropdown toggle is an icon button; axe's button-name (critical) on
        // the editor caught it unnamed — the adornment name must not be the textbox's label
        // (PostsPageTests' rule for adornment buttons).
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, Model()));

        var toggle = cut.Find("button.mud-input-adornment-icon-button");
        toggle.GetAttribute("aria-label").Should().Be("Category options");
    }

    [Fact]
    public async Task Category_ClearingThePicker_UnsetsTheModelCategory()
    {
        // The old category select offered an explicit "None" option; the picker must still let a
        // category go once chosen (or loaded), or a post can never drop its category again.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var category = new CategoryDto(Guid.CreateVersion7(), "Tech", "tech", null, 1);
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, model));
        var autocomplete = cut.FindComponent<MudAutocomplete<CategoryDto>>().Instance;
        await cut.InvokeAsync(() => autocomplete.ValueChanged.InvokeAsync(category));
        model.CategoryId.Should().Be(category.Id);

        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Clear").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
        {
            model.Category.Should().BeNull("the picker must let the category go again");
            model.CategoryId.Should().BeNull("a cleared category must not travel with the save request");
        });
    }

    [Fact]
    public async Task Category_PickerOffersEverySearchedMatch_NotJustTheFirstFew()
    {
        // The picker's search fetches a bounded page of matches and the dropdown must offer every
        // one it fetched. A clip below the fetch hides matches with no way to reach them by
        // browsing — including the category the user just created, which sorts wherever the name
        // order puts it.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var categories = Enumerable.Range(1, 15)
            .Select(n => new CategoryDto(Guid.CreateVersion7(), $"Category {n:00}", $"category-{n:00}", null, 0))
            .ToList();

        // WORKAROUND(W-074, MudBlazor): dropdowns render through MudPopoverProvider and MudAutocomplete searches on focus
        // MudBlazor renders dropdowns through the popover provider (MainLayout renders one in
        // the app), so the test tree needs it too for the options to have a home.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<PostEditorForm>(1);
            builder.AddAttribute(2, nameof(PostEditorForm.Model), model);
            builder.AddAttribute(3, nameof(PostEditorForm.CategorySearch),
                (Func<string, CancellationToken, Task<IEnumerable<CategoryDto>>>)
                ((_, _) => Task.FromResult<IEnumerable<CategoryDto>>(categories)));
            builder.CloseComponent();
        });

        // Open the picker the way the browser does (a click focuses the input; the search runs
        // on focus) and wait for the search's matches to render.
        var autocomplete = cut.FindComponent<MudAutocomplete<CategoryDto>>();
        await cut.InvokeAsync(() =>
            autocomplete.Find("input").TriggerEventAsync("onfocus", new FocusEventArgs()));
        // END WORKAROUND(W-074)

        await cut.WaitForAssertionAsync(() =>
            cut.FindAll("[role=option]").Select(o => o.TextContent.Trim()).Should()
                .Contain("Category 15", "every match the search fetched must be offered"));
    }

    [Fact]
    public async Task Category_PickingOne_SetsTheModelCategoryId()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var category = new CategoryDto(Guid.CreateVersion7(), "Tech", "tech", null, 1);
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, model));

        var autocomplete = cut.FindComponent<MudAutocomplete<CategoryDto>>();
        await cut.InvokeAsync(() => autocomplete.Instance.ValueChanged.InvokeAsync(category));

        model.CategoryId.Should().Be(category.Id,
            "the picker is the single-choice control over the category search, bound to the model");
    }

    [Fact]
    public async Task Category_PickerOptions_ShowTheCategorysIconAndName()
    {
        // The pre-03i select listed each category's icon beside its name; the autocomplete's
        // options must offer the same. The icon must be the one the category carries — a fixed
        // placeholder icon next to every row would look right and tell the user nothing.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var categories = new List<CategoryDto>
            { new(Guid.CreateVersion7(), "Tech", "tech", "Landscape", 3) };

        // WORKAROUND(W-074, MudBlazor): dropdowns render through MudPopoverProvider and MudAutocomplete searches on focus
        // MudBlazor renders dropdowns through the popover provider (MainLayout renders one in
        // the app), so the test tree needs it too for the options to have a home.
        var cut = Render(builder =>
        {
            builder.OpenComponent<MudPopoverProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<PostEditorForm>(1);
            builder.AddAttribute(2, nameof(PostEditorForm.Model), model);
            builder.AddAttribute(3, nameof(PostEditorForm.CategorySearch),
                (Func<string, CancellationToken, Task<IEnumerable<CategoryDto>>>)
                ((_, _) => Task.FromResult<IEnumerable<CategoryDto>>(categories)));
            builder.CloseComponent();
        });

        // Open the picker the way the browser does (a click focuses the input; the search runs
        // on focus) and wait for the search's match to render.
        var autocomplete = cut.FindComponent<MudAutocomplete<CategoryDto>>();
        await cut.InvokeAsync(() =>
            autocomplete.Find("input").TriggerEventAsync("onfocus", new FocusEventArgs()));
        // END WORKAROUND(W-074)

        await cut.WaitForAssertionAsync(() =>
        {
            var option = cut.FindAll("[role=option]").Single(o => o.TextContent.Contains("Tech"));
            var svg = option.QuerySelector("svg.category-icon");
            svg.Should().NotBeNull("the option must render an icon for its category");
            svg!.QuerySelectorAll("path").Select(p => p.GetAttribute("d")).Should()
                .Equal(PathData(Icons.Material.Filled.Landscape),
                    "the option must show the icon its category carries");
            option.TextContent.Should().Contain("Tech", "the option must name its category");
        });
    }

    /// <summary>The <c>d</c> geometry of an icon constant (SVG markup); comparing it ignores the
    /// test renderer's self-closing-tag style.</summary>
    private static string[] PathData(string iconMarkup) =>
        [.. Regex.Matches(iconMarkup, "d=\"([^\"]+)\"").Select(m => m.Groups[1].Value)];

    [Fact]
    public void PostEditorForm_PublishDatePicker_ShowsTheIsoDateFor8October2026()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model,
            Model(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc))));

        var label = cut.FindAll("label").Single(l => l.TextContent == "Publish date (UTC)");
        var shown = cut.Find($"#{label.GetAttribute("for")}").GetAttribute("value");

        shown.Should().Be("2026-10-08",
            "numeric dates must be ISO 8601 (yyyy-MM-dd), never US or UK day-month order");
    }

    [Fact]
    public void PostEditorForm_ScheduleDisabledForPastDate()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, model));

        var schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");
        schedule.HasAttribute("disabled").Should().BeTrue("5 October is behind the clock");

        model.PublishedAtDate = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
        cut.Render();

        schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");
        schedule.HasAttribute("disabled").Should().BeFalse("the entered UTC moment is in the future");
    }

    private static readonly DateTime StoredPublishedAt = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(PostStatus.Draft, false)]
    [InlineData(PostStatus.Scheduled, false)]
    [InlineData(PostStatus.Published, true)]
    public void PostEditorForm_SetDate_ShownOnlyForPublishedPosts(PostStatus status, bool shown)
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(8, 0, 0)))
            .Add(x => x.Status, status)
            .Add(x => x.StoredPublishedAt, StoredPublishedAt));

        cut.FindAll("button").Any(b => b.TextContent == "Set date").Should().Be(shown);
    }

    [Fact]
    public void PostEditorForm_SetDate_EnabledForAPastMomentThatDiffersFromTheStoredOne_AndRaisesItsCallback()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var calls = new List<string>();
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(8, 0, 0)))
            .Add(x => x.Status, PostStatus.Published)
            .Add(x => x.StoredPublishedAt, StoredPublishedAt)
            .Add(x => x.OnSetDate, EventCallback.Factory.Create(this, () => calls.Add("setdate"))));

        var button = cut.FindAll("button").Single(b => b.TextContent == "Set date");
        button.HasAttribute("disabled").Should().BeFalse();
        button.HasAttribute("aria-describedby").Should().BeFalse();
        button.Click(new MouseEventArgs());

        cut.WaitForAssertion(() => calls.Should().Equal("setdate"));
    }

    [Theory]
    [InlineData(2026, 10, 1, 9, 0, "the same moment as the stored one")]
    [InlineData(2026, 12, 1, 9, 0, "a future moment (that is Schedule's job)")]
    public void PostEditorForm_SetDate_DisabledWithItsHint(int year, int month, int day, int hour, int minute, string why)
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(hour, minute, 0)))
            .Add(x => x.Status, PostStatus.Published)
            .Add(x => x.StoredPublishedAt, StoredPublishedAt));

        var button = cut.FindAll("button").Single(b => b.TextContent == "Set date");
        button.HasAttribute("disabled").Should().BeTrue(why);
        cut.Find($"#{button.GetAttribute("aria-describedby")}").TextContent.Trim().Should()
            .Be("Set date needs a past date and time that differs from the published one.");
    }

    [Fact]
    public void PostEditorForm_ScheduleDisabledWithoutADate()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, Model()));

        var schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");
        schedule.HasAttribute("disabled").Should().BeTrue("without a date there is no moment to schedule for");
    }

    [Theory]
    [InlineData(PostStatus.Published, true)]
    [InlineData(PostStatus.Scheduled, true)]
    [InlineData(PostStatus.Draft, false)]
    public void PostEditorForm_Unpublish_ShownOnlyForPublishedOrScheduled(PostStatus status, bool shown)
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.Status, status));

        cut.FindAll("button").Any(b => b.TextContent == "Unpublish").Should().Be(shown);
    }

    [Fact]
    public void PostEditorForm_ActionButtons_RaiseTheirCallbacks()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var calls = new List<string>();
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0)))
            .Add(x => x.Status, PostStatus.Published)
            .Add(x => x.OnSave, EventCallback.Factory.Create(this, () => calls.Add("save")))
            .Add(x => x.OnPublishNow, EventCallback.Factory.Create(this, () => calls.Add("publish")))
            .Add(x => x.OnSchedule, EventCallback.Factory.Create(this, () => calls.Add("schedule")))
            .Add(x => x.OnUnpublish, EventCallback.Factory.Create(this, () => calls.Add("unpublish"))));

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());
        cut.FindAll("button").Single(b => b.TextContent == "Publish now").Click(new MouseEventArgs());
        cut.FindAll("button").Single(b => b.TextContent == "Schedule").Click(new MouseEventArgs());
        cut.FindAll("button").Single(b => b.TextContent == "Unpublish").Click(new MouseEventArgs());

        cut.WaitForAssertion(() => calls.Should().Equal("save", "publish", "schedule", "unpublish"));
    }

    [Fact]
    public void PostEditorForm_Tags_SuggestsExistingTagNames()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        IReadOnlyList<TagDto> tags = [new(Guid.CreateVersion7(), "k8s", "k8s", 2, true)];
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.TagSuggestions, tags));

        cut.Find("#post-tag-suggestions").InnerHtml.Should().Contain("value=\"k8s\"",
            "the chip input autocompletes from the existing tags");
    }

    [Fact]
    public async Task PostEditorForm_Tags_ChipInput_AddsAndRemoves()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        IReadOnlyList<TagDto> tags = [new(Guid.CreateVersion7(), "k8s", "k8s", 2, true)];
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.TagSuggestions, tags));

        cut.Find("#post-tag-entry").Input("hiking");
        cut.FindAll("button").Single(b => b.TextContent == "Add tag").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => model.Tags.Should().Equal("hiking"));
        cut.Find(".post-form__tag").TextContent.Should().Contain("hiking");

        cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Remove tag hiking").Click(new MouseEventArgs());
        await cut.WaitForAssertionAsync(() => model.Tags.Should().BeEmpty());
    }

    [Fact]
    public async Task PostEditorForm_Tags_SuggestionsDropTheNamesAddedAsChips()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        IReadOnlyList<TagDto> tags =
            [new(Guid.CreateVersion7(), "k8s", "k8s", 2, true), new(Guid.CreateVersion7(), "yaml", "yaml", 1, true)];
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.TagSuggestions, tags));

        cut.Find("#post-tag-suggestions").InnerHtml.Should().Contain("value=\"k8s\"");
        cut.Find("#post-tag-entry").Input("k8s");
        cut.FindAll("button").Single(b => b.TextContent == "Add tag").Click(new MouseEventArgs());

        // A name already added as a chip is not a suggestion; the other tags still are.
        await cut.WaitForAssertionAsync(() =>
            cut.Find("#post-tag-suggestions").InnerHtml.Should().NotContain("value=\"k8s\"",
                "a tag already added as a chip must not be offered again"));
        cut.Find("#post-tag-suggestions").InnerHtml.Should().Contain("value=\"yaml\"");
    }

    [Fact]
    public void PostEditorForm_OverTheContentLimit_EverySaveActionIsDisabled()
    {
        // While the content is over the 200,000-character limit the API refuses the save, so
        // every action that saves the form first (Save draft, Publish now, Schedule, Unpublish)
        // must be disabled — with the Schedule otherwise offered (a future moment).
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        model.MarkdownContent = new string('a', 200_001);
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        foreach (var name in new[] { "Save draft", "Publish now", "Schedule", "Unpublish" })
        {
            cut.FindAll("button").Single(b => b.TextContent == name).HasAttribute("disabled").Should()
                .BeTrue($"'{name}' saves the form and the API would refuse over-limit content");
        }
    }

    [Fact]
    public void PostEditorForm_ScheduleDisabledForOverLongContent_LinksItsReason()
    {
        // Brief 13i item 6: a disabled button must say why. The date is in the future, so the over-long
        // content is the only reason, and the explanation is the editor's own over-limit line.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        model.MarkdownContent = new string('a', 200_001);
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        var schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");

        var reasons = DescribedBy(cut, schedule);
        reasons.Should().ContainSingle("only the content limit disables it").Which.Should().Contain("characters or fewer");
    }

    [Fact]
    public void PostEditorForm_ScheduleDisabledForPastDateAndOverLongContent_LinksBothReasons()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        model.MarkdownContent = new string('a', 200_001);
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        var schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");

        DescribedBy(cut, schedule).Should().HaveCount(2).And.Contain(text => text.Contains("future"))
            .And.Contain(text => text.Contains("characters or fewer"));
    }

    [Fact]
    public void PostEditorForm_ScheduleEnabled_HasNoDescription()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        cut.FindAll("button").Single(b => b.TextContent == "Schedule").HasAttribute("aria-describedby").Should().BeFalse();
    }

    [Theory]
    [InlineData("Save draft")]
    [InlineData("Publish now")]
    [InlineData("Unpublish")]
    public void PostEditorForm_ASaveActionDisabledForOverLongContent_LinksItsReason(string button)
    {
        // Brief 14b item 4 (the 13g review's Minor): the same pattern as Schedule. The content limit is the
        // only thing that disables these three, and its explanation is the editor's own over-limit line.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        model.MarkdownContent = new string('a', 200_001);
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        var element = cut.FindAll("button").Single(b => b.TextContent == button);

        element.HasAttribute("disabled").Should().BeTrue("the content is over the limit");
        DescribedBy(cut, element).Should().ContainSingle().Which.Should().Contain("characters or fewer");
    }

    [Theory]
    [InlineData("Save draft")]
    [InlineData("Publish now")]
    [InlineData("Unpublish")]
    public void PostEditorForm_ASaveActionThatIsEnabled_HasNoDescription(string button)
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        var element = cut.FindAll("button").Single(b => b.TextContent == button);

        element.HasAttribute("disabled").Should().BeFalse();
        element.HasAttribute("aria-describedby").Should().BeFalse();
    }

    /// <summary>The text of every element the button's aria-describedby names; a name with no element fails the test.</summary>
    private static List<string> DescribedBy(IRenderedComponent<PostEditorForm> cut, AngleSharp.Dom.IElement button)
    {
        var ids = (button.GetAttribute("aria-describedby") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. ids.Select(id => cut.Find($"[id='{id}']").TextContent)];
    }

    [Fact]
    public void PostEditorForm_AtTheContentLimit_EverySaveActionIsEnabled()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc), new TimeSpan(10, 0, 0));
        model.MarkdownContent = new string('a', 200_000);
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.Status, PostStatus.Published));

        foreach (var name in new[] { "Save draft", "Publish now", "Schedule", "Unpublish" })
        {
            cut.FindAll("button").Single(b => b.TextContent == name).HasAttribute("disabled").Should()
                .BeFalse($"exactly at the limit '{name}' must still work");
        }
    }

    [Fact]
    public void PageMode_HidesCategoryTagsAndBanner()
    {
        // A page carries no category, tags or banner (the API rejects all three), so the page
        // editor must not offer the fields — offering them invites a 400 on save.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.IsPage, true));

        cut.FindComponents<MudAutocomplete<CategoryDto>>().Should().BeEmpty("pages have no category");
        cut.FindAll("#post-tag-entry").Should().BeEmpty("pages have no tags");
        cut.FindComponents<BannerPicker>().Should().BeEmpty("pages have no banner");
        cut.FindComponents<MarkdownEditor>().Should().ContainSingle("the page's body is still edited here");
    }

    [Fact]
    public void PageMode_ShowsThePlacementControlAndOrder_BoundToTheModel()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.IsPage, true));

        cut.Find("label[for=post-nav-placement]").TextContent.Trim().Should().Be("Show in");
        cut.FindAll("#post-nav-placement option").Select(o => o.TextContent.Trim())
            .Should().Equal("Nowhere", "Sidebar", "Footer");
        cut.Find("label[for=post-nav-order]").TextContent.Trim().Should().Be("Order (lowest first)");

        cut.Find("#post-nav-placement").Change("Footer");
        cut.Find("#post-nav-order").Change("3");

        model.NavigationPlacement.Should().Be(NavigationPlacement.Footer, "the select is the model's NavigationPlacement");
        model.NavigationOrder.Should().Be(3, "the number field is the model's NavigationOrder");

        model.NavigationPlacement = NavigationPlacement.Sidebar;
        model.NavigationOrder = 7;
        cut.Render();

        cut.Find("#post-nav-placement option[value=Sidebar]").HasAttribute("selected").Should().BeTrue(
            "the select renders the model's placement back");
        cut.Find("#post-nav-order").GetAttribute("value").Should().Be("7",
            "the number field renders the model's order back");
    }

    [Fact]
    public void PostMode_HidesTheNavigationFields()
    {
        // Post mode is unchanged: the page-only fields must not appear on a post.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, Model()));

        cut.FindComponents<MudAutocomplete<CategoryDto>>().Should().ContainSingle();
        cut.FindAll("#post-tag-entry").Should().ContainSingle();
        cut.FindComponents<BannerPicker>().Should().ContainSingle();
        cut.FindAll("#post-nav-placement").Should().BeEmpty("posts have no placement");
        cut.FindAll("#post-nav-order").Should().BeEmpty();
    }

    [Fact]
    public void PostEditorForm_RemovingTheBanner_ClearsIt_AndReportsTheModelChange()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var banner = new MediaAssetDto(Guid.CreateVersion7(), "hills", "hills.png", "image/png",
            "/api/media/hills", MediaAssetScope.Global, null, null);
        model.Banner = banner;
        model.BannerImageId = banner.Id;
        var changes = 0;
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, model)
            .Add(x => x.OnModelChanged, EventCallback.Factory.Create(this, () => changes++)));

        cut.FindAll("button").Single(b => b.TextContent == "Remove").Click(new MouseEventArgs());

        model.Banner.Should().BeNull();
        model.BannerImageId.Should().BeNull();
        changes.Should().Be(1, "the page keeps the Preview in step with the model through OnModelChanged");
    }

    [Fact]
    public void StatusRegion_IsAlwaysRendered_AsAPoliteStatus_AndCarriesTheSavedTime()
    {
        // Brief 13g item 6 (review I-4): a live region only announces what changes inside it, so it
        // must already be in the page when the first save lands.
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, Model()));

        var region = cut.Find("[role=status].post-actions__saved");
        region.TextContent.Trim().Should().BeEmpty("nothing has been saved yet");

        cut.Render(p => p.Add(x => x.SavedAt, new DateTime(2026, 10, 6, 9, 5, 0, DateTimeKind.Utc)));

        cut.Find("[role=status].post-actions__saved").TextContent.Trim().Should().Be("Saved 09:05",
            "the same element, so the change is announced");
    }

    [Fact]
    public void StatusRegion_ShowsTheAnnouncementInPlaceOfTheSavedTime()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p
            .Add(x => x.Model, Model())
            .Add(x => x.SavedAt, new DateTime(2026, 10, 6, 9, 5, 0, DateTimeKind.Utc))
            .Add(x => x.Announcement, "Published"));

        cut.Find("[role=status].post-actions__saved").TextContent.Trim().Should().Be("Published");
    }

    [Fact]
    public async Task TagEntry_Enter_AddsTheTag_LikeTheAddTagButton()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, model));

        var entry = cut.Find("#post-tag-entry");
        await entry.InputAsync(new ChangeEventArgs { Value = "kubernetes" });
        await entry.KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });

        model.Tags.Should().Equal("kubernetes");
        cut.FindAll(".post-form__tag").Should().ContainSingle().Which.TextContent.Should().Contain("kubernetes");
        cut.Find("#post-tag-entry").GetAttribute("value").Should().BeNullOrEmpty("the entry is cleared for the next tag");
    }

    [Fact]
    public async Task TagEntry_OtherKeys_AddNothing()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var model = Model();
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, model));

        var entry = cut.Find("#post-tag-entry");
        await entry.InputAsync(new ChangeEventArgs { Value = "kubernetes" });
        await entry.KeyDownAsync(new KeyboardEventArgs { Key = "a" });

        model.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Schedule_WhenDisabledForNoFutureDate_IsDescribedByVisibleText()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, Model()));

        var schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");
        schedule.HasAttribute("disabled").Should().BeTrue();
        var hint = cut.Find($"#{schedule.GetAttribute("aria-describedby")}");
        hint.TextContent.Trim().Should().Be("Schedule needs a publish date and time in the future.");
    }

    [Fact]
    public void Schedule_WhenEnabled_HasNoHint()
    {
        Services.AddSingleton<TimeProvider>(ClockAt(12));
        var cut = Render<PostEditorForm>(p => p.Add(x => x.Model, Model(new DateTime(2026, 12, 1), new TimeSpan(9, 0, 0))));

        var schedule = cut.FindAll("button").Single(b => b.TextContent == "Schedule");
        schedule.HasAttribute("disabled").Should().BeFalse();
        schedule.HasAttribute("aria-describedby").Should().BeFalse();
        cut.FindAll(".post-actions__hint").Should().BeEmpty();
    }
}
