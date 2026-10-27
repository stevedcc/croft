namespace Croft.Web.E2E;

/// <summary>
/// Brief 14c items 1 and 7: what a first visit downloads. A fresh browser context (an empty cache)
/// loads the home page and a post with two images at 1440 x 900 and 390 x 844 and the numbers come
/// from the browser's own network events: requests, encoded body bytes in total and per kind, and the
/// font URLs fetched. They are written to the test output (<c>FIRST-VISIT</c> lines: the baseline of
/// item 1 and the numbers of item 7), and held to a budget.
/// <para>
/// The budget is the number measured after brief 14c (the table below) plus 10 %, with the bytes
/// rounded up to the next 5 KB (1 KB = 1024 bytes) and a count rounded up to a whole request. It
/// holds the three things 14c gained: only the faces a page uses are downloaded (font bytes and font
/// requests), as Latin subsets, with the HTML compressed (total). A change that costs a visitor more
/// must raise a number here on purpose: measure, change the "after" figure, say why. Since brief 18a
/// the home page's total excludes image bytes (the cards' images depend on the suite's data); the
/// post page's total is unchanged.
/// </para>
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FirstVisitMeasurementTests : FirstVisitTest
{
    /// <summary>What a first visit downloaded when brief 14c was finished: total bytes, font bytes, font requests.</summary>
    private sealed record After(long TotalBytes, long FontBytes, int FontRequests);

    // Brief 18a (issue #82) redefined the home figure: the home page shows whatever posts the instance holds, with their card
    // images, so its total moved with the other classes' data and with worker scheduling. For home, TotalBytes below is the
    // total MINUS image bytes (the image bytes are written to the FIRST-VISIT line, not asserted); the post page keeps its total,
    // its content being the test's own. Measured alone on a fresh instance (no posts, so 650 image bytes of the shell):
    // home 1440: total 440,650 - image 650 = 440,000; home 390: 440,652 - 650 = 440,002. Budget: 440,000 x 1.1 = 484,000, rounded
    // up to the next 5 KB (5,120) = 486,400 (BytesBudget). Font bytes, font requests and the post figures are unchanged.
    // Brief 16a re-measured the totals on .NET 11 RC 2 (the fonts are unchanged): ASP.NET Core 11's
    // blazor.web.js is 245,494 bytes instead of 200,645, and a Debug build (what the suite runs)
    // now sends it as an effectively uncompressed gzip stream (245,562 bytes; .NET 10: 55,644).
    // Brief 14c's figures were home 349,885 / 349,883 and post 492,176 / 492,177.
    private static readonly Dictionary<(string Page, int Width), After> Measured = new()
    {
        [("home", 1440)] = new(440_000, 147_824, 5),
        [("home", 390)] = new(440_002, 147_824, 5),
        [("post", 1440)] = new(520_409, 183_444, 6),
        [("post", 390)] = new(520_396, 183_444, 6),
    };

    private const long FiveKilobytes = 5 * 1024;

    /// <summary>The measured number plus 10 %, rounded up to the next 5 KB.</summary>
    private static long BytesBudget(long measured) => (long)Math.Ceiling(measured * 1.1 / FiveKilobytes) * FiveKilobytes;

    /// <summary>The measured count plus 10 %, rounded up to a whole request.</summary>
    private static int CountBudget(int measured) => (int)Math.Ceiling(measured * 1.1);

    [Test]
    public async Task AFirstVisit_IsMeasured_ForTheHomePageAndAPost_AtBothViewports_AndStaysInBudget()
    {
        var fixtures = await EnsureFixturesAsync();
        foreach (var (page, path) in new[] { ("home", "/"), ("post", fixtures.PostPath) })
        {
            foreach (var (width, height) in Viewports)
            {
                var visit = await VisitAsync(path, width, height);
                TestContext.Progress.WriteLine($"FIRST-VISIT {visit}");

                // The measurement must have measured something real.
                Assert.That(visit.Requests, Is.GreaterThan(3), $"{page} at {width}: the load made almost no requests.");
                Assert.That(visit.BytesByKind.GetValueOrDefault("document"), Is.GreaterThan(0), $"{page} at {width}: no document bytes.");
                Assert.That(visit.FontUrls, Is.Not.Empty, $"{page} at {width}: no font was fetched.");
                if (page == "post")
                {
                    Assert.That(visit.RequestsByKind.GetValueOrDefault("image"), Is.GreaterThanOrEqualTo(2),
                        $"{page} at {width}: the post's two images were not fetched.");
                }

                var after = Measured[(page, width)];
                // Home: the total without image bytes (the cards' images are the suite's data, not the page's); post: the total.
                var comparedBytes = page == "home" ? visit.TotalBytes - visit.BytesByKind.GetValueOrDefault("image") : visit.TotalBytes;
                var fontRequests = visit.RequestsByKind.GetValueOrDefault("font");
                Assert.Multiple(() =>
                {
                    Assert.That(comparedBytes, Is.LessThanOrEqualTo(BytesBudget(after.TotalBytes)),
                        $"{page} at {width}: a first visit downloaded {comparedBytes} bytes{(page == "home" ? " without images" : string.Empty)}, budget {BytesBudget(after.TotalBytes)} ({visit}).");
                    Assert.That(visit.FontBytes, Is.LessThanOrEqualTo(BytesBudget(after.FontBytes)),
                        $"{page} at {width}: fonts were {visit.FontBytes} bytes, budget {BytesBudget(after.FontBytes)}: {string.Join(", ", visit.FontUrls.Select(FaceFile))}.");
                    Assert.That(fontRequests, Is.LessThanOrEqualTo(CountBudget(after.FontRequests)),
                        $"{page} at {width}: {fontRequests} font requests, budget {CountBudget(after.FontRequests)}: {string.Join(", ", visit.FontUrls.Select(FaceFile))}.");
                });
            }
        }
    }
}
