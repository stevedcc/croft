namespace Croft.Web.E2E;

/// <summary>
/// Brief 09f item 6: a tap on the pinned outline row still counts when the finger wanders a
/// little. For touch pointers the move that makes a gesture a drag is 10 px (Euclidean) — iOS
/// still fires the click for a tap that moved up to about that much — while mouse and pen keep
/// 3 px; the 48 px pull threshold is unchanged. The gestures are dispatched as touch-type
/// pointer events (pointerdown, pointermoves, pointerup, then the click that follows), the way
/// the WebKit probe does; the outcome is the drawer's aria-expanded.
/// </summary>
[Parallelizable(ParallelScope.Fixtures)]
public class OutlineTouchSlopTests : StaticReadingTest
{
    private static readonly SemaphoreSlim DataGate = new(1, 1);
    private static readonly string RunId = Guid.NewGuid().ToString("N")[..12];

    private static bool _dataReady;
    private static Published _post = null!;

    [Test]
    public async Task TouchTapThatWandersSixPixels_TogglesTheDrawer_OpenThenClosed()
    {
        await using var context = await NewPhoneContextAsync();
        var page = await OpenPinnedRowAsync(context);
        var row = page.Locator(".toc-inline__row");

        await GestureAsync(page, "touch", dx: 6, dy: 0);
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "true");

        await GestureAsync(page, "touch", dx: 6, dy: 0);
        await Expect(row).ToHaveAttributeAsync("aria-expanded", "false");
    }

    [Test]
    public async Task TouchGestureThatWandersTwelvePixels_ChangesNothing()
    {
        await using var context = await NewPhoneContextAsync();
        var page = await OpenPinnedRowAsync(context);

        await GestureAsync(page, "touch", dx: 12, dy: 0);

        await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
    }

    [Test]
    public async Task TouchPullOfSixtyPixelsDown_OpensTheDrawerExactlyOnce()
    {
        await using var context = await NewPhoneContextAsync();
        var page = await OpenPinnedRowAsync(context);

        await GestureAsync(page, "touch", dx: 0, dy: 60);

        // Opened by the pull, and the click that follows the pull must not toggle it shut again.
        await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "true");
    }

    [Test]
    public async Task MouseGestureThatMovesSixPixels_ChangesNothing()
    {
        await using var context = await NewPhoneContextAsync();
        var page = await OpenPinnedRowAsync(context);

        await GestureAsync(page, "mouse", dx: 6, dy: 0);

        await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
    }

    /// <summary>The phone page of the fixture post, scrolled until the outline is the pinned row.</summary>
    private async Task<IPage> OpenPinnedRowAsync(IBrowserContext context)
    {
        await EnsureFixturesAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{BaseUrl}/post/{_post.Slug}");
        await WaitForInteractiveAsync(page);
        await page.EvaluateAsync("() => document.fonts.ready");
        await page.EvaluateAsync("() => document.querySelectorAll('.markdown-body h2')[3].scrollIntoView()");
        await Expect(page.Locator(".toc-inline")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("toc-inline--pinned"));
        await Expect(page.Locator(".toc-inline__row")).ToHaveAttributeAsync("aria-expanded", "false");
        // The pin's height transition (200 ms) has run when no animation is left on the panel.
        await page.WaitForFunctionAsync("() => document.querySelector('.toc-inline').getAnimations().length === 0");
        return page;
    }

    /// <summary>pointerdown at the row's centre, pointermoves in 1 px steps to (dx, dy), pointerup,
    /// then the click a browser fires after a gesture that ends on the element.</summary>
    private static async Task GestureAsync(IPage page, string pointerType, double dx, double dy) =>
        await page.EvaluateAsync(
            """
            ([pointerType, dx, dy]) => {
                const row = document.querySelector('.toc-inline__row');
                const box = row.getBoundingClientRect();
                const x = box.left + box.width / 2;
                const y = box.top + box.height / 2;
                const fire = (type, cx, cy) => row.dispatchEvent(new PointerEvent(type, {
                    pointerType, pointerId: 7, isPrimary: true, bubbles: true, cancelable: true,
                    clientX: cx, clientY: cy,
                }));
                fire('pointerdown', x, y);
                const steps = Math.max(1, Math.round(Math.hypot(dx, dy)));
                for (let i = 1; i <= steps; i++) fire('pointermove', x + dx * i / steps, y + dy * i / steps);
                fire('pointerup', x + dx, y + dy);
                row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: x + dx, clientY: y + dy }));
            }
            """,
            new object[] { pointerType, dx, dy });

    private async Task EnsureFixturesAsync()
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

            _post = await PublishAsync($"Slop Post {RunId}", BodyWithSections(RunId, 7),
                [$"slop{RunId}a", $"slop{RunId}b"]);
            _dataReady = true;
        }
        finally
        {
            DataGate.Release();
        }
    }
}
