namespace Croft.Web.E2E;

/// <summary>
/// Brief 10b item 1: the Featured card's media box fills the card at every width. The box has
/// aspect-ratio 21/9 and max-height 420px; without an explicit width, CSS carries the maximum
/// height through the ratio into a maximum width of 980px, and on a wider card the box stops short
/// and leaves white. Outcomes measured in the browser: the box's width equals the card's inner
/// width, its height is at most 420px; on a phone the box is 4:3 and full width.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class FeaturedCardFitTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];
    private static bool _dataReady;

    [TestCase(1440, 900)]
    [TestCase(1920, 1080)]
    public async Task FeaturedMedia_FillsTheCardWidth_AndStaysUnder420Tall(int width, int height)
    {
        var (card, media) = await MeasureAsync(width, height);

        Assert.That(media.Width, Is.EqualTo(card.Width).Within(1),
            $"the Featured media box is {media.Width:0.##}px wide in a {card.Width:0.##}px card at {width}px: it must fill the card");
        Assert.That(media.Height, Is.LessThanOrEqualTo(420 + 1), "the media box is at most 420px tall");
        Assert.That(media.Height, Is.EqualTo(Math.Min(card.Width * 9 / 21, 420)).Within(1),
            "the height follows the 21:9 ratio, capped at 420px");
    }

    [Test]
    public async Task FeaturedMedia_OnAPhone_IsFourByThreeAndFullWidth()
    {
        var (card, media) = await MeasureAsync(390, 844);

        Assert.That(media.Width, Is.EqualTo(card.Width).Within(1), "full width of the card");
        Assert.That(media.Height, Is.EqualTo(media.Width * 3 / 4).Within(1), "4:3");
    }

    private sealed record Size(double Width, double Height);

    private async Task<(Size Card, Size Media)> MeasureAsync(int width, int height)
    {
        await EnsureFixtureAsync();
        await using var context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new() { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync(BaseUrl);
        var media = page.Locator(".post-card--featured .post-card__media");
        await Expect(media.Locator("img")).ToBeVisibleAsync();
        var json = await page.EvaluateAsync<double[]>(
            @"() => {
                const card = document.querySelector('.post-card--featured');
                const media = card.querySelector('.post-card__media');
                const cs = getComputedStyle(card);
                const inner = card.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
                const r = media.getBoundingClientRect();
                return [inner, card.getBoundingClientRect().height, r.width, r.height];
            }");
        return (new Size(json[0], json[1]), new Size(json[2], json[3]));
    }

    private async Task EnsureFixtureAsync()
    {
        await DataGate.WaitAsync();
        try
        {
            if (_dataReady)
            {
                return;
            }

            var banner = await UploadBannerAsync($"featured-fit-{RunId}.png");
            await PublishAsync($"Featured fit {RunId}", BodyWithSections(RunId, 1), bannerImageId: banner);
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
