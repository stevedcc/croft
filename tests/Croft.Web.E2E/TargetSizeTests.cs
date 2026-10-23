namespace Croft.Web.E2E;

/// <summary>
/// Brief 13g item 4 (review I-2, M-7): the small targets are at least 24 x 24 CSS px (WCAG 2.5.8),
/// measured from the rendered boxes at phone width: the category chip on a post card and the remove
/// button of an editor tag.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class TargetSizeTests : StaticReadingTest
{
    private const double MinimumTarget = 24;
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    [TestCase("/")]
    [TestCase("category")]
    [TestCase("search")]
    public async Task CategoryChip_OnAPostCard_IsAtLeast24By24_At390(string where)
    {
        var category = await CreateCategoryAsync($"Chip {RunId}");
        await PublishAsync($"Chip Post {RunId}", $"Chip body {RunId}.", categoryId: category.Id);
        await Page.SetViewportSizeAsync(390, 844);
        var path = where switch
        {
            "category" => $"/category/{category.Slug}",
            "search" => $"/search?q={RunId}",
            _ => where,
        };
        await Page.GotoAsync($"{BaseUrl}{path}");
        await WaitForInteractiveAsync(Page);

        var chip = Page.Locator(".post-card .category-chip", new PageLocatorOptions { HasText = $"Chip {RunId}" }).First;
        await Expect(chip).ToBeVisibleAsync();
        var box = await chip.BoundingBoxAsync() ?? throw new InvalidOperationException("the chip has no layout box");
        Assert.That(box.Height, Is.GreaterThanOrEqualTo(MinimumTarget), $"the category chip is {box.Width:0.##} x {box.Height:0.##} px on {path}");
        Assert.That(box.Width, Is.GreaterThanOrEqualTo(MinimumTarget), $"the category chip is {box.Width:0.##} x {box.Height:0.##} px on {path}");
    }

    [Test]
    public async Task TagRemoveButton_InThePostEditor_IsAtLeast24By24_At390()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await LoginAsync("admin@example.com", "password");
        await OpenNewPostEditorAsync();
        await AddTagsAsync($"tag{RunId}");

        var remove = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = $"Remove tag tag{RunId}" });
        await Expect(remove).ToBeVisibleAsync();
        var box = await remove.BoundingBoxAsync() ?? throw new InvalidOperationException("the remove button has no layout box");
        Assert.That(box.Height, Is.GreaterThanOrEqualTo(MinimumTarget), $"the tag remove button is {box.Width:0.##} x {box.Height:0.##} px");
        Assert.That(box.Width, Is.GreaterThanOrEqualTo(MinimumTarget), $"the tag remove button is {box.Width:0.##} x {box.Height:0.##} px");
    }
}
