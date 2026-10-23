using AwesomeAssertions;
using Bunit;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The editor's Preview renders the Markdown when the text has changed and been still for the
/// debounce window (150 ms), not on every keystroke and not in a property read on every render
/// (brief 13e item 5, review I-5). The clock is a manual one: nothing sleeps.
/// </summary>
public class PostEditPagePreviewDebounceTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Now);
    private readonly CountingRenderer _renderer = new(new MarkdownRenderer());

    private void Setup()
    {
        Services.AddSingleton<IBlogApiClient>(new FakeBlogApiClient());
        Services.AddSingleton<TimeProvider>(_clock);
        Services.AddSingleton<IMarkdownRenderer>(_renderer);
    }

    [Fact]
    public async Task TypingTenCharacters_RendersTheMarkdownOnce_AfterTheWindow_AndThePreviewShowsTheFinalText()
    {
        Setup();
        var cut = Render<PostEditPage>();
        var before = _renderer.Calls;

        const string Typed = "tenchars!!";
        for (var length = 1; length <= Typed.Length; length++)
        {
            cut.Find("textarea[aria-label='Markdown']").Input("**" + Typed[..length] + "**");
        }

        await cut.WaitForAssertionAsync(() => cut.FindComponent<Croft.Web.Components.Admin.PostEditorForm>().Instance.Model
            .MarkdownContent.Should().Be("**tenchars!!**"));
        (_renderer.Calls - before).Should().BeLessThanOrEqualTo(Typed.Length / 2,
            "ten keystrokes inside the window must not render ten times");
        cut.Find(".markdown-body").InnerHtml.Should().NotContain("tenchars",
            "the preview still shows the text from before the typing until the window passes");

        await _clock.AdvanceAsync(TimeSpan.FromMilliseconds(150), expectTimers: 10);

        await cut.WaitForAssertionAsync(() =>
            cut.Find(".markdown-body").InnerHtml.Should().Contain("<strong>tenchars!!</strong>"));
        (_renderer.Calls - before).Should().Be(1, "one render for the whole burst");
    }

    [Fact]
    public async Task APreviewRenderedForTheSameText_IsNotRenderedAgainByLaterRenders()
    {
        Setup();
        var cut = Render<PostEditPage>();
        cut.Find("textarea[aria-label='Markdown']").Input("Some text");
        await _clock.AdvanceAsync(TimeSpan.FromMilliseconds(150), expectTimers: 1);
        await cut.WaitForAssertionAsync(() => cut.Find(".markdown-body").InnerHtml.Should().Contain("Some text"));
        var before = _renderer.Calls;

        for (var i = 0; i < 5; i++)
        {
            cut.Render();
        }

        _renderer.Calls.Should().Be(before, "the Markdown is rendered when it changes, not when the page renders");
    }

    private sealed class CountingRenderer(IMarkdownRenderer inner) : IMarkdownRenderer
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public RenderedMarkdown Render(string markdown) => Render(markdown, new MarkdownRenderOptions());

        public RenderedMarkdown Render(string markdown, MarkdownRenderOptions options)
        {
            Interlocked.Increment(ref _calls);
            return inner.Render(markdown, options);
        }
    }
}
