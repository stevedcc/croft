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
/// must raise a number here on purpose: measure, change the "after" figure, say why.
/// </para>
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FirstVisitMeasurementTests : FirstVisitTest
{
    /// <summary>What a first visit downloaded when brief 14c was finished: total bytes, font bytes, font requests.</summary>
    private sealed record After(long TotalBytes, long FontBytes, int FontRequests);

    private static readonly Dictionary<(string Page, int Width), After> Measured = new()
    {
        [("home", 1440)] = new(349_885, 147_824, 5),
        [("home", 390)] = new(349_883, 147_824, 5),
        [("post", 1440)] = new(492_176, 183_444, 6),
        [("post", 390)] = new(492_177, 183_444, 6),
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
                var fontRequests = visit.RequestsByKind.GetValueOrDefault("font");
                Assert.Multiple(() =>
                {
                    Assert.That(visit.TotalBytes, Is.LessThanOrEqualTo(BytesBudget(after.TotalBytes)),
                        $"{page} at {width}: a first visit downloaded {visit.TotalBytes} bytes, budget {BytesBudget(after.TotalBytes)} ({visit}).");
                    Assert.That(visit.FontBytes, Is.LessThanOrEqualTo(BytesBudget(after.FontBytes)),
                        $"{page} at {width}: fonts were {visit.FontBytes} bytes, budget {BytesBudget(after.FontBytes)}: {string.Join(", ", visit.FontUrls.Select(FaceFile))}.");
                    Assert.That(fontRequests, Is.LessThanOrEqualTo(CountBudget(after.FontRequests)),
                        $"{page} at {width}: {fontRequests} font requests, budget {CountBudget(after.FontRequests)}: {string.Join(", ", visit.FontUrls.Select(FaceFile))}.");
                });
            }
        }
    }
}
