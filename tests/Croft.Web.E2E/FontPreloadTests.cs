namespace Croft.Web.E2E;

/// <summary>
/// Brief 14c item 3: the page preloads exactly the faces every reading page uses. A preload is a
/// download the browser starts before it knows the page needs the face; a face a page does not use
/// is wasted bytes (and Chromium says so in the console). The faces every page loads are measured
/// here, from <c>document.fonts</c>, on the home page, a post with italic, bold and inline code, a
/// category page and a tag page, at both viewports; the preload list must be exactly that set.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FontPreloadTests : FirstVisitTest
{
    private async Task<List<Visit>> VisitEveryPageAtAsync(int width, int height, bool watchPreloadWarning)
    {
        var fixtures = await EnsureFixturesAsync();
        return [.. await Task.WhenAll(fixtures.Paths.Select(path => VisitAsync(path, width, height, watchPreloadWarning: watchPreloadWarning)))];
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task ThePreloadedFaces_AreExactlyTheFacesEveryReadingPageLoads(int width, int height)
    {
        var visits = await VisitEveryPageAtAsync(width, height, watchPreloadWarning: false);
        foreach (var visit in visits)
        {
            TestContext.Progress.WriteLine(
                $"LOADED-FACES {visit.Path} @{width}: {string.Join("; ", visit.LoadedFaces.Select(f => f.ToString()).Order())}");
        }

        var onEveryPage = visits.Select(v => v.LoadedFaces.ToHashSet()).Aggregate((all, next) => [.. all.Intersect(next)]);
        foreach (var visit in visits)
        {
            var preloaded = visit.PreloadUrls.Select(url => FaceOfFile[FaceFile(url)]).ToHashSet();
            Assert.That(preloaded, Is.EquivalentTo(onEveryPage),
                $"{visit.Path} at {width}: the preloaded faces must be the faces every page loads " +
                $"({string.Join(", ", onEveryPage.Select(f => f.ToString()).Order())}); " +
                $"loaded here: {string.Join(", ", visit.LoadedFaces.Select(f => f.ToString()).Order())}.");
        }
    }

    [TestCase(1440, 900)]
    [TestCase(390, 844)]
    public async Task EveryPreloadedFace_IsLoadedOnThePage_AndTheBrowserWarnsOfNoWastedPreload(int width, int height)
    {
        var visits = await VisitEveryPageAtAsync(width, height, watchPreloadWarning: true);
        foreach (var visit in visits)
        {
            var wasted = visit.PreloadUrls.Select(url => FaceOfFile[FaceFile(url)]).Where(f => !visit.LoadedFaces.Contains(f)).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(wasted, Is.Empty, $"{visit.Path} at {width}: preloaded but not loaded: {string.Join(", ", wasted)}.");
                Assert.That(visit.ConsoleWarnings.Where(w => w.Contains("preload", StringComparison.OrdinalIgnoreCase)), Is.Empty,
                    $"{visit.Path} at {width}: Chromium logged a preload warning.");
            });
        }
    }
}
