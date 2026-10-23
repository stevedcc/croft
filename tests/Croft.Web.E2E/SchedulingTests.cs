namespace Croft.Web.E2E;

[Parallelizable(ParallelScope.Fixtures)]
public class SchedulingTests : WebTest
{
    [Test]
    public async Task EditorScheduling_CombinesDateTimeAsUtc_AndApiReturnsExactValue()
    {
        // Login as admin
        await LoginAsync("admin@example.com", "password");

        // Navigate to new post editor (route migration: /admin → /admin/posts/new)
        await OpenNewPostEditorAsync();

        // Fill required fields
        var title = $"Scheduling E2E {Guid.NewGuid():N}";
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).FillAsync(title);
        await Page.GetByLabel("Title", new PageGetByLabelOptions { Exact = true }).BlurAsync();
        await Page.GetByLabel("Markdown").FillAsync("Scheduling test body.");
        await Page.GetByLabel("Markdown").BlurAsync();

        // --- Set date via MudDatePicker popup ---
        // Fixed absolute target: 15 June 2030. This avoids a time-bomb where a
        // relative "N months forward" target drifts into the wrong year.
        const string targetDay = "15";
        const string targetMonthYear = "June 2030";   // MudBlazor header format
        const string expectedDateValue = "2030-06-15"; // yyyy-MM-dd, ISO 8601
        const string expectedTimeValue = "15:30";

        await Page.GetByLabel("Publish date (UTC)").ClickAsync();
        var pickerOverlay = Page.Locator(".mud-picker-open, .mud-popover-open").First;
        await pickerOverlay.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        // Navigate forward until the calendar header shows the target month/year.
        // The header is a <p> with mud-typography-align-center inside the calendar header.
        var monthHeader = pickerOverlay.Locator(".mud-picker-calendar-header p.mud-typography-align-center");
        var nextBtn = pickerOverlay.Locator(".mud-picker-calendar-header button:last-child");

        for (int safety = 0; safety < 120; safety++) // 120 months = 10 years max
        {
            var headerText = (await monthHeader.TextContentAsync() ?? string.Empty).Trim();
            if (headerText.Contains(targetMonthYear, StringComparison.OrdinalIgnoreCase))
                break;

            // Remember current header before clicking next
            var before = headerText;
            await nextBtn.ClickAsync();

            // Wait for the header text to actually change (concrete condition, no sleep)
            await Page.WaitForFunctionAsync(
                @"(args) => {
                    const el = document.querySelector('.mud-picker-calendar-header p.mud-typography-align-center');
                    return el && !el.textContent.includes(args.before);
                }",
                new { before },
                new PageWaitForFunctionOptions { Timeout = 3000 });
        }

        // Verify we landed on the right month
        var finalHeader = (await monthHeader.TextContentAsync() ?? string.Empty).Trim();
        Assert.That(finalHeader, Does.Contain(targetMonthYear),
            $"Calendar header should show {targetMonthYear} but showed: {finalHeader}");

        // Click on the target day
        var dayButtons = pickerOverlay.Locator($".mud-picker-calendar-day:has-text('{targetDay}')");
        await dayButtons.Last.ClickAsync();

        // Wait for date picker to close
        await Page.Locator(".mud-picker-open, .mud-popover-open").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });

        // --- Set time via MudTimePicker popup (24-hour format) ---
        await Page.GetByLabel("Publish time (UTC)").ClickAsync();
        var timePicker = Page.Locator(".mud-picker-open, .mud-popover-open").First;
        await timePicker.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        // WORKAROUND(W-073, MudBlazor): the clock is a pointer-event mask placed on a requestAnimationFrame; clicks use element-relative points
        // MudBlazor's clock is a mask of pointer-event wedges. The ring points are clicked as
        // element-relative points on the mask, never as raw page coordinates: the popover is
        // placed on a requestAnimationFrame after its open class lands, so a bounding box read
        // as soon as the picker is "visible" can still be the unplaced (-9999px) one and send a
        // raw click nowhere. A locator click waits for the mask to be placed and stable and
        // derives the point from its box at click time.
        var clockMask = timePicker.Locator(".mud-picker-time-clock-mask");
        var box = await clockMask.BoundingBoxAsync();
        Assert.That(box, Is.Not.Null, "Clock mask bounding box not found");

        var width = (double)box.Width;
        var height = (double)box.Height;

        // Hour 15: 90 degrees = 3 o'clock, outer ring (~75% of radius)
        await clockMask.ClickAsync(new LocatorClickOptions
        {
            Position = new() { X = (float)(width / 2 + width / 2 * 0.75), Y = (float)(height / 2) },
        });

        // Wait for minute dial to become visible (concrete condition)
        await timePicker.Locator(".mud-time-picker-minute:not(.mud-time-picker-dial-hidden)").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 5000 });

        // Minute 30: 180 degrees = 6 o'clock, outer ring (~75% of radius)
        await clockMask.ClickAsync(new LocatorClickOptions
        {
            Position = new() { X = (float)(width / 2), Y = (float)(height / 2 + width / 2 * 0.75) },
        });

        // Wait for picker to close (concrete condition — the overlay detaches)
        await Page.Locator(".mud-picker-open, .mud-popover-open").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 5000 });
        // END WORKAROUND(W-073)

        // --- Schedule (the entered date is the future moment to publish at) ---
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Schedule" }).ClickAsync();

        // Wait for navigation to the post editor with the new id
        try
        {
            await Page.WaitForURLAsync(
                url => url.StartsWith($"{BaseUrl}/admin/posts/") && url.EndsWith("/edit"),
                new PageWaitForURLOptions { Timeout = 15000 });
        }
        catch (TimeoutException)
        {
            var url = Page.Url;
            var html = await Page.ContentAsync();
            throw new TimeoutException($"Schedule did not navigate to the post editor. Current URL: {url}. Page content:\n{html}");
        }

        var postId = PostIdFromEditorUrl();
        TestContext.Out.WriteLine($"Scheduled post id: {postId}");

        // --- Verify round-trip: navigate to the edit page and check date/time pickers ---
        // This proves the full path: MudTimePicker binding -> UtcDateTime.Combine
        // -> Blazor component -> IBlogApiClient -> API -> Postgres -> API -> editor display.
        await Page.GotoAsync($"{BaseUrl}/admin/posts/{postId}/edit");
        await WaitForInteractiveAsync(Page);

        // Read the date picker input value
        var dateInput = Page.GetByLabel("Publish date (UTC)");
        var dateValue = await dateInput.InputValueAsync();
        TestContext.Out.WriteLine($"Date picker value: {dateValue}");

        // Read the time picker input value
        var timeInput = Page.GetByLabel("Publish time (UTC)");
        var timeValue = await timeInput.InputValueAsync();
        TestContext.Out.WriteLine($"Time picker value: {timeValue}");

        // Assert exact date (day + month + year) — a wrong-month selection must fail
        Assert.That(dateValue, Is.EqualTo(expectedDateValue),
            $"Date picker should show {expectedDateValue} but showed: {dateValue}");

        // Assert exact time
        Assert.That(timeValue, Is.EqualTo(expectedTimeValue),
            $"Time picker should show {expectedTimeValue} but showed: {timeValue}");

        // Cleanup: the test post remains in the dev database (same pattern as
        // PostCreationTests). The Web app does not proxy /api/posts to the API,
        // so there is no authenticated path to delete from the browser context.
        // Orchestration or manual cleanup removes these between runs.
    }
}
