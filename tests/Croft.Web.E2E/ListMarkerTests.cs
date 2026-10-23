using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// List markers must stay inside the text column. The global CSS reset zeroes list padding and
/// the post body's styles never set it back, so bulleted and numbered markers rendered left of
/// the paragraph edge — outside the column on desktop and across the screen edge on phones.
/// The fix gives the lists left padding, so the marker area (the padding strip between the list
/// box's left edge and its content edge, where outside markers render) sits inside the column
/// and the item text is indented one step from the paragraph edge. A list's li box starts at the
/// content edge — markers live outside it — so the measurement is the ul/ol left edge plus its
/// computed padding-left, compared with the .markdown-body column, not the li box.
///
/// Fixture rules (as in FluidTypeTests/ContentWrapperTests): a run-keyed unique post published
/// through the admin UI once per run, shared by every test in the class, with a bulleted list
/// (wrapping a nested list), a numbered list and a paragraph to anchor the column edge.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ListMarkerTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly string Title = $"Lists {RunId}";

    private static bool _dataReady;
    private static string _slug = string.Empty;

    [TestCase(390)]
    [TestCase(1920)]
    public async Task ListMarkers_LieInsideTheTextColumn(int width)
    {
        var measurement = await MeasureListsAsync(width);

        // The fixture must actually render both kinds of list before any geometry means anything.
        var top = measurement.Lists.Where(l => l.ParentLeft is null).ToList();
        Assert.That(top.Where(l => l.Tag == "ul"), Is.Not.Empty, "the fixture post has no top-level bulleted list.");
        Assert.That(top.Where(l => l.Tag == "ol"), Is.Not.Empty, "the fixture post has no top-level numbered list.");

        foreach (var list in top)
        {
            // Outside markers render in the strip between the list box's left edge and its content
            // edge. It must start at or right of the column edge and be wide enough to hold the
            // marker (a bullet or two digits plus gap is ~1em), otherwise the marker is drawn left
            // of the strip — left of the column.
            var markerLeft = list.Left;
            var markerRight = list.Left + list.PaddingLeft;

            Assert.That(markerLeft, Is.GreaterThanOrEqualTo(measurement.ColumnLeft - 0.5),
                $"the {list.Tag} box at {width}px starts at {markerLeft:0.##}px, left of the text column's left edge {measurement.ColumnLeft:0.##}px.");
            Assert.That(markerRight, Is.LessThanOrEqualTo(measurement.ColumnRight + 0.5),
                $"the {list.Tag} marker area at {width}px ends at {markerRight:0.##}px, right of the text column's right edge {measurement.ColumnRight:0.##}px.");
            // The geometry above is the room reserved for the markers; a marker that was removed
            // (list-style: none, or items that are no longer list items) reserves the same room.
            Assert.That(list.ListStyleType, Is.Not.EqualTo("none").And.Not.Empty,
                $"the {list.Tag} at {width}px shows no markers: its items have list-style-type '{list.ListStyleType}'.");
            Assert.That(list.LiDisplay, Is.EqualTo("list-item"),
                $"the {list.Tag} items at {width}px are '{list.LiDisplay}', and only list-item boxes paint a marker.");
            Assert.That(list.PaddingLeft, Is.GreaterThanOrEqualTo(list.FontSize),
                $"the {list.Tag} at {width}px reserves only {list.PaddingLeft:0.##}px for its markers (1em is {list.FontSize:0.##}px): outside markers render left of the content edge, so without at least 1em of left padding they hang outside the text column.");
        }
    }

    [TestCase(390)]
    [TestCase(1920)]
    public async Task NestedList_IndentsOneMoreStep(int width)
    {
        var measurement = await MeasureListsAsync(width);

        var nested = measurement.Lists.Where(l => l.ParentLeft is not null).ToList();
        Assert.That(nested, Is.Not.Empty, "the fixture post has no nested list.");

        foreach (var list in nested)
        {
            Assert.That(list.Left, Is.GreaterThanOrEqualTo(list.ParentLeft!.Value + list.FontSize),
                $"the nested {list.Tag} at {width}px starts at {list.Left:0.##}px, less than one step (1em = {list.FontSize:0.##}px) right of its parent list's left edge {list.ParentLeft.Value:0.##}px.");
            Assert.That(list.PaddingLeft, Is.GreaterThanOrEqualTo(list.FontSize),
                $"the nested {list.Tag} at {width}px reserves only {list.PaddingLeft:0.##}px for its markers (1em is {list.FontSize:0.##}px); its markers would hang outside the list box.");
        }
    }

    /// <summary>One list's geometry: its border-box left/right edges, the computed padding-left
    /// (the marker strip) and the li font size that resolves the em threshold. ParentLeft is the
    /// enclosing list's left edge for a nested list, null for a top-level one.</summary>
    private sealed record ListGeometry(string Tag, double Left, double Right, double PaddingLeft, double FontSize, string ListStyleType, string LiDisplay, double? ParentLeft);

    private sealed record ColumnMeasurement(double ColumnLeft, double ColumnRight, List<ListGeometry> Lists);

    private async Task<ColumnMeasurement> MeasureListsAsync(int width)
    {
        await EnsureFixtureAsync();
        await Page.SetViewportSizeAsync(width, 900);
        await Page.GotoAsync($"{BaseUrl}/post/{_slug}");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Expect(Page.Locator(".markdown-body")).ToBeVisibleAsync();
        await Page.EvaluateAsync("() => document.fonts.ready");

        var json = await Page.EvaluateAsync<string>(
            @"() => {
                const body = document.querySelector('.markdown-body');
                const col = body.getBoundingClientRect();
                const lists = [...body.querySelectorAll('ul, ol')].map(el => {
                    const r = el.getBoundingClientRect();
                    const li = el.querySelector(':scope > li');
                    const parent = el.parentElement.closest('ul, ol');
                    return {
                        tag: el.tagName.toLowerCase(),
                        left: r.left,
                        right: r.right,
                        paddingLeft: parseFloat(getComputedStyle(el).paddingLeft),
                        fontSize: li ? parseFloat(getComputedStyle(li).fontSize) : parseFloat(getComputedStyle(el).fontSize),
                        listStyleType: li ? getComputedStyle(li).listStyleType : '',
                        liDisplay: li ? getComputedStyle(li).display : '',
                        parentLeft: parent ? parent.getBoundingClientRect().left : null
                    };
                });
                return JSON.stringify({ columnLeft: col.left, columnRight: col.right, lists });
            }");

        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;
        var lists = new List<ListGeometry>();
        foreach (var el in root.GetProperty("lists").EnumerateArray())
        {
            lists.Add(new ListGeometry(
                el.GetProperty("tag").GetString()!,
                el.GetProperty("left").GetDouble(),
                el.GetProperty("right").GetDouble(),
                el.GetProperty("paddingLeft").GetDouble(),
                el.GetProperty("fontSize").GetDouble(),
                el.GetProperty("listStyleType").GetString()!,
                el.GetProperty("liDisplay").GetString()!,
                el.GetProperty("parentLeft").ValueKind == JsonValueKind.Null ? null : el.GetProperty("parentLeft").GetDouble()));
        }

        return new ColumnMeasurement(root.GetProperty("columnLeft").GetDouble(), root.GetProperty("columnRight").GetDouble(), lists);
    }

    private async Task EnsureFixtureAsync()
    {
        if (_dataReady)
        {
            return;
        }

        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            await LoginAsync("admin@example.com", "password");

            await OpenNewPostEditorAsync();

            var body =
                $"Fixture intro {RunId}.\n\n" +
                "## Lists section\n\n" +
                $"Paragraph before the lists {RunId}, anchoring the paragraph edge the list markers are compared against.\n\n" +
                $"- Alpha item {RunId}\n" +
                "- Beta item with enough words to wrap onto a second line on a narrow phone screen\n" +
                "  - Nested one\n" +
                "  - Nested two\n" +
                "- Gamma item\n\n" +
                "1. First numbered step\n" +
                "2. Second numbered step\n" +
                "3. Third numbered step";

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(Title);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(body);
            await Page.GetByLabel("Markdown").BlurAsync();

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 30000 });

            _slug = await SlugForPostAsync(PostIdFromEditorUrl());

            await Page.GotoAsync($"{BaseUrl}/logout");
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
