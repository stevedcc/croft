using Croft.Web.Components.Admin;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

namespace Croft.Web.Tests.Components;

/// <summary>
/// MarkdownEditor (spec §6.2/§7.4): a textarea with a toolbar. The toolbar inserts text at the
/// caret through the blogEditor.insertAtCursor JS helper, and the "Insert image" control hands
/// the chosen file to the host, which uploads it and asks the editor to insert the markdown.
/// </summary>
public class MarkdownEditorTests : BlogBunitContext
{
    [Fact]
    public async Task MarkdownEditor_InsertImage_InsertsMarkdownAtCursor()
    {
        var requested = new List<IBrowserFile>();
        var bound = default(string);
        var cut = default(Bunit.IRenderedComponent<MarkdownEditor>);
        var handle = default(Func<IBrowserFile, Task>);
        JSInterop.Setup<string?>("blogEditor.insertAtCursor", _ => true)
            .SetResult("Hello world![a.png](/api/media/1)");

        cut = Render<MarkdownEditor>(p => p
            .Add(x => x.Value, "Hello world")
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, v => bound = v))
            .Add(x => x.OnInsertImageRequested, EventCallback.Factory.Create<IBrowserFile>(this, f => handle!(f))));

        handle = async file =>
        {
            requested.Add(file);
            // The host's half of the flow: upload the chosen file, then insert its markdown.
            await cut!.Instance.InsertAtCursorAsync($"![{file.Name}](/api/media/1)");
        };

        var input = cut.FindComponent<InputFile>();
        await cut.InvokeAsync(() =>
            input.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([new FakeBrowserFile("a.png")])));

        requested.Should().ContainSingle("the chosen file is handed to the host for upload")
            .Which.Name.Should().Be("a.png");

        var insert = JSInterop.Invocations["blogEditor.insertAtCursor"].Should().ContainSingle(
            "the markdown is inserted through the JS helper, at the caret").Subject;
        insert.Arguments[1].Should().Be("![a.png](/api/media/1)");

        bound.Should().Be("Hello world![a.png](/api/media/1)",
            "the bound value follows the textarea's new content");
    }

    [Fact]
    public async Task MarkdownEditor_ToolbarButtons_InsertTheirSnippetsAtTheCursor()
    {
        JSInterop.Setup<string?>("blogEditor.insertAtCursor", _ => true).SetResult("done");
        var cut = Render<MarkdownEditor>(p => p
            .Add(x => x.Value, "Hello world")
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, v => { })));

        cut.FindAll("button").Single(b => b.TextContent == "Bold").Click(new MouseEventArgs());
        cut.FindAll("button").Single(b => b.TextContent == "Heading").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            JSInterop.Invocations["blogEditor.insertAtCursor"].Should().HaveCount(2));

        JSInterop.Invocations["blogEditor.insertAtCursor"].Select(i => i.Arguments[1]).Should()
            .Equal("**bold**", "## ");
    }

    [Fact]
    public void MarkdownEditor_CountLine_AppearsOnlyOver90PercentOfTheLimit()
    {
        // The count is quiet at ordinary lengths and starts warning at 90% of the 200,000
        // character limit: hidden at exactly 180,000, shown at 180,001.
        var atThreshold = Render<MarkdownEditor>(p => p.Add(x => x.Value, new string('a', 180_000)));

        atThreshold.FindAll(".md-editor__count").Should().BeEmpty(
            "at exactly 90% of the limit the count has not started showing yet");

        var over = Render<MarkdownEditor>(p => p.Add(x => x.Value, new string('a', 180_001)));

        over.Find(".md-editor__count").TextContent.Should().Be("180,001 / 200,000 characters",
            "the count shows used / limit with thousands separators, below the textarea");
    }

    [Fact]
    public void MarkdownEditor_AtTheLimit_NoOverLimitAlert()
    {
        var cut = Render<MarkdownEditor>(p => p.Add(x => x.Value, new string('a', 200_000)));

        cut.FindAll(".md-editor__over").Should().BeEmpty("exactly at the limit is not over it");
        cut.Find(".md-editor__count").TextContent.Should().Be("200,000 / 200,000 characters");
    }

    [Fact]
    public async Task MarkdownEditor_UserTyping_IsNeverRewrittenByItsOwnRenders()
    {
        // A render diff that writes the textarea's value back clobbers characters typed while
        // the batch was in flight (measured: burst typing into a 60K post lost 7 of 20
        // characters to stale rewrites). User typing reaches the host but is never carried in
        // the render tree back to the textarea; only external value changes are.
        string? bound = null;
        var cut = Render<MarkdownEditor>(p => p
            .Add(x => x.Value, "Loaded text.")
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, v => bound = v)));

        cut.Find("textarea").Input("Loaded text. typed");

        await cut.WaitForAssertionAsync(() => bound.Should().Be("Loaded text. typed"));
        cut.Find("textarea").GetAttribute("value").Should().Be("Loaded text.",
            "the render tree must not carry the user's typing back over the textarea");
    }

    [Fact]
    public async Task MarkdownEditor_AnExternalValueChange_IsRenderedIntoTheTextarea()
    {
        var cut = Render<MarkdownEditor>(p => p.Add(x => x.Value, "first"));

        await cut.InvokeAsync(() => cut.Instance.SetParametersAsync(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(MarkdownEditor.Value)] = "second" })));

        cut.Find("textarea").GetAttribute("value").Should().Be("second",
            "a value arriving from outside the textarea must still be rendered into it");
    }

    [Fact]
    public async Task MarkdownEditor_InputOverTheLimit_Alerts_AndKeepsEveryCharacter()
    {
        // The limit must never lose text: what the user typed reaches the host unchanged and
        // stays in the textarea; the editor only warns (role=alert) and lets the page disable
        // saving.
        string? bound = null;
        var cut = Render<MarkdownEditor>(p => p
            .Add(x => x.Value, string.Empty)
            .Add(x => x.ValueChanged, EventCallback.Factory.Create<string>(this, v => bound = v)));

        var over = new string('a', 200_001);
        cut.Find("textarea").Input(over);

        await cut.WaitForAssertionAsync(() =>
            bound.Should().Be(over, "an over-limit text is still delivered to the host in full"));
        var alert = cut.Find(".md-editor__over");
        alert.GetAttribute("role").Should().Be("alert", "the over-limit error announces itself");
        alert.TextContent.Should().Contain("Content must be 200,000 characters or fewer.");
        cut.Find(".md-editor__count").TextContent.Should().Be("200,001 / 200,000 characters");
    }
}
