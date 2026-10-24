using System.Text.Json;

namespace Croft.Web.E2E;

/// <summary>
/// Brief 03l item 2: the reading column must be measured in the reading font, and the
/// text-plus-rail block centred under the hero where there is room for it. "ch" is the width
/// of "0" in the element's own font, so a measure declared on the UI font at the root size
/// holds far fewer characters of the body text (Croft Serif at 1.125rem, 1.0625rem below
/// 960px) than the design's 68/88. Outcomes asserted: the column is 68ch/88ch of the body's
/// own font and size at 1440px and 1920px viewport width — so a line of body text holds
/// about 68/88 characters — and at those widths the text-plus-rail block sits centred under
/// the hero with equal gaps (the ≥1000px content-width rule replaced 03f's 80rem gate).
///
/// Tolerances. The ch measure compares the column against the serif's own "0" advance and
/// allows ±2ch: the clamp's middle ramp is px-based (calc cannot divide a length by a
/// length), so the exact 88ch depends on where the ramp meets the cap, and Chromium rounds
/// the "0" advance to whole pixels at reading sizes (9px at 18px, 10px at 19.45px). The
/// prose measure divides the column by the long paragraph's average character advance and
/// allows ±12%: "characters" in the design means ch (zero-widths), and this font's prose
/// averages 0.92 of a zero per character (measured), so a ch-defined measure reads 8-9%
/// high when counted in prose characters — the band covers that mix plus the advance
/// rounding. Both bands reject the UI-font measure by a clear margin (break-it-once: the
/// column drops to ~53/~76 prose characters and ~60/~70 body ch at 1440/1920).
///
/// Fixtures (the suite's rule): a run-keyed unique post published through the admin UI once
/// per run, shared by every test in the class: one long prose paragraph (the characters-per-
/// line sample), two headings so the rail's TOC renders (so the two-column geometry is
/// exercised at ≥1000px content width) and a tag so the rail has content.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class ReadingMeasureTests : WebTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static readonly string Title = $"Reading {RunId}";
    private static readonly string TagName = $"reading{DateTime.UtcNow.Ticks % 1000000:D6}";

    private static bool _dataReady;
    private static string _slug = string.Empty;

    [TestCase(1440, 68)]
    [TestCase(1920, 88)]
    public async Task TextColumn_IsMeasuredInBodyFontCh(int width, int expectedCh)
    {
        var m = await MeasureAsync(width);

        // WORKAROUND(W-071, Chromium): Chromium rounds the zero advance to whole pixels, hence the 2ch tolerance
        Assert.That(m.ColumnWidth, Is.EqualTo(expectedCh * m.BodyChPx).Within(2 * m.BodyChPx),
            $"the text column at {width}px should measure {expectedCh}ch of the body font " +
            $"({expectedCh * m.BodyChPx:0.##}px at 1ch = {m.BodyChPx:0.##}px), was {m.ColumnWidth:0.##}px " +
            $"({m.ColumnWidth / m.BodyChPx:0.##} body ch, {m.ColumnWidth / m.LayoutChPx:0.##} layout ch; " +
            $"layout font {m.LayoutFontFamily} at {m.LayoutFontSize:0.##}px).");
        // END WORKAROUND(W-071)
    }

    [TestCase(1440, 68)]
    [TestCase(1920, 88)]
    public async Task BodyLine_HoldsAboutNCharacters(int width, int expectedChars)
    {
        var m = await MeasureAsync(width);

        // The paragraph must be long enough that its average is prose, not a headline.
        Assert.That(m.ParagraphLength, Is.GreaterThan(300),
            $"the fixture's long paragraph has only {m.ParagraphLength} characters; the average needs prose.");

        var charsPerLine = m.ColumnWidth / m.AvgCharPx;
        Assert.That(charsPerLine, Is.EqualTo((double)expectedChars).Within(expectedChars * 0.12),
            $"a line of body text at {width}px should hold about {expectedChars} characters " +
            $"(±12%: real prose glyphs average {m.AvgCharPx:0.####}px against a 0-width of {m.BodyChPx:0.##}px, " +
            $"so a ch-defined line counts {100 * m.BodyChPx / m.AvgCharPx:0.#}% high in prose characters), " +
            $"but the {m.ColumnWidth:0.##}px column holds {charsPerLine:0.#}.");
    }

    [TestCase(1440)]
    [TestCase(1920)]
    public async Task TextPlusRail_IsCentredUnderTheHero(int width)
    {
        var m = await MeasureAsync(width);

        // The rule applies where the block is narrower than the hero; assert that precondition
        // so a future full-width block does not make the gap comparison vacuous.
        Assert.That(m.BlockWidth, Is.LessThan(m.HeroWidth - 4),
            $"at {width}px the text-plus-rail block ({m.BlockWidth:0.##}px) is not narrower than the hero ({m.HeroWidth:0.##}px); the centring rule does not apply.");
        Assert.That(Math.Abs(m.LeftGap - m.RightGap), Is.LessThanOrEqualTo(2),
            $"the text-plus-rail block at {width}px is not centred under the hero: left gap {m.LeftGap:0.##}px, right gap {m.RightGap:0.##}px.");
    }

    [Test]
    public async Task RailAndToc_KeepTheirUiFonts()
    {
        // The layout grid is declared in the reading font (so its ch means body characters),
        // but the rail and TOC must keep their UI fonts and sizes exactly as they were: the
        // rail has always rendered at the shell's body size (MudBlazor's 0.875rem baseline,
        // which body inherits) and must not pick up the body text's 1.125rem.
        var m = await MeasureAsync(1440);

        Assert.That(m.RailFontFamily, Does.Contain("Croft Sans"),
            $"the rail now renders in '{m.RailFontFamily}', not the UI font.");
        Assert.That(m.RailFontSize, Is.EqualTo(0.875 * m.Rem).Within(0.01),
            $"the rail's {m.RailFontSize:0.##}px text is no longer the shell body size ({0.875 * m.Rem:0.##}px); it must not scale with the body text's font.");
        Assert.That(m.TocFontFamily, Does.Contain("Croft Sans"),
            $"the TOC now renders in '{m.TocFontFamily}', not the UI font.");
        Assert.That(m.TocFontSize, Is.EqualTo(13.0).Within(0.01),
            $"the TOC's {m.TocFontSize:0.##}px text is no longer its designed 13px.");
    }

    /// <summary>One page's geometry: the text column's width, the "0" advance of the body font
    /// at the body size (the measure's unit) and of the layout's own font (diagnostics), the
    /// long paragraph's average character advance, and the hero/text/rail edges the centring
    /// gaps are measured from.</summary>
    private sealed record ReadingGeometry(
        double ColumnWidth,
        double BodyChPx,
        double LayoutChPx,
        double LayoutFontSize,
        string LayoutFontFamily,
        double AvgCharPx,
        int ParagraphLength,
        double HeroWidth,
        double BlockWidth,
        double LeftGap,
        double RightGap,
        string RailFontFamily,
        double RailFontSize,
        string TocFontFamily,
        double TocFontSize,
        double Rem);

    private async Task<ReadingGeometry> MeasureAsync(int width)
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
                const layout = document.querySelector('.post-layout');
                const layoutStyle = getComputedStyle(layout);

                // 1ch of the body text's own font and size — the unit the measure must mean.
                const chProbe = document.createElement('span');
                chProbe.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;width:1ch;padding:0;margin:0;border:0;';
                body.appendChild(chProbe);
                const bodyCh = chProbe.getBoundingClientRect().width;
                chProbe.style.fontFamily = layoutStyle.fontFamily;
                chProbe.style.fontSize = layoutStyle.fontSize;
                const layoutCh = chProbe.getBoundingClientRect().width;
                chProbe.remove();

                // The long paragraph's own text on one line: its average character advance.
                const p = [...body.querySelectorAll('p')].find(el => el.textContent.length > 300);
                const textProbe = document.createElement('span');
                textProbe.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;padding:0;margin:0;border:0;';
                textProbe.textContent = p.textContent;
                body.appendChild(textProbe);
                const avgChar = textProbe.getBoundingClientRect().width / p.textContent.length;
                const paragraphLength = p.textContent.length;
                textProbe.remove();

                const hero = document.querySelector('.post-hero').getBoundingClientRect();
                const rail = document.querySelector('.post-rail').getBoundingClientRect();
                const railStyle = getComputedStyle(document.querySelector('.post-rail'));
                const toc = document.querySelector('.post-rail .toc');
                const tocStyle = toc ? getComputedStyle(toc) : null;

                return JSON.stringify({
                    columnWidth: col.width,
                    bodyCh,
                    layoutCh,
                    layoutFontSize: parseFloat(layoutStyle.fontSize),
                    layoutFontFamily: layoutStyle.fontFamily,
                    avgChar,
                    paragraphLength,
                    heroWidth: hero.width,
                    blockWidth: rail.right - col.left,
                    leftGap: col.left - hero.left,
                    rightGap: hero.right - rail.right,
                    railFontFamily: railStyle.fontFamily,
                    railFontSize: parseFloat(railStyle.fontSize),
                    tocFontFamily: tocStyle ? tocStyle.fontFamily : '(no toc)',
                    tocFontSize: tocStyle ? parseFloat(tocStyle.fontSize) : 0,
                    rem: parseFloat(getComputedStyle(document.documentElement).fontSize)
                });
            }");

        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        return new ReadingGeometry(
            r.GetProperty("columnWidth").GetDouble(),
            r.GetProperty("bodyCh").GetDouble(),
            r.GetProperty("layoutCh").GetDouble(),
            r.GetProperty("layoutFontSize").GetDouble(),
            r.GetProperty("layoutFontFamily").GetString()!,
            r.GetProperty("avgChar").GetDouble(),
            r.GetProperty("paragraphLength").GetInt32(),
            r.GetProperty("heroWidth").GetDouble(),
            r.GetProperty("blockWidth").GetDouble(),
            r.GetProperty("leftGap").GetDouble(),
            r.GetProperty("rightGap").GetDouble(),
            r.GetProperty("railFontFamily").GetString()!,
            r.GetProperty("railFontSize").GetDouble(),
            r.GetProperty("tocFontFamily").GetString()!,
            r.GetProperty("tocFontSize").GetDouble(),
            r.GetProperty("rem").GetDouble());
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

            // One long prose paragraph (the average-character sample), two headings so the
            // rail's TOC renders, and text for both sections.
            var body =
                $"Reading measure fixture {RunId}, a deliberately long paragraph of ordinary prose " +
                "words with typical letter widths and spaces, no headings or links or code to distort " +
                "the mix, so the average character width it yields is the reading font's own and the " +
                "line length the column allows can be measured against the text that actually runs " +
                "through it rather than against a synthetic sample of one repeated glyph.\n\n" +
                "## First section\n\n" +
                $"First section body {RunId} with enough words to fill the line comfortably.\n\n" +
                "## Second section\n\n" +
                $"Second section body {RunId} with enough words to fill the line comfortably.";

            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(Title);
            await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
            await Page.GetByLabel("Markdown").FillAsync(body);
            await Page.GetByLabel("Markdown").BlurAsync();
            await AddTagsAsync(TagName);

            await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Publish now" }).ClickAsync();
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 30000 });

            _slug = await SlugForPostAsync(PostIdFromEditorUrl());

            await SignOutAsync();
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
