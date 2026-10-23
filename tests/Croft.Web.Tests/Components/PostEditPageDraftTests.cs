using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// PostEditPage's draft-backup wiring (D3's guaranteed layer): the editor reads the local backup
/// on load and acts on the decision — a differing backup is offered with its local time (Restore
/// / Discard), an identical one is deleted silently, and a successful save clears the backup
/// through the JS module (which then keys future backups to the post id).
/// </summary>
public class PostEditPageDraftTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTime LoadedContentEdit =
        new(2026, 10, 6, 11, 0, 0, DateTimeKind.Utc);

    private FakeBlogApiClient _api = null!;

    private FakeTimeProvider Setup()
    {
        _api = new FakeBlogApiClient();
        var clock = new FakeTimeProvider(Now);
        Services.AddSingleton<IBlogApiClient>(_api);
        Services.AddSingleton<IMarkdownRenderer, MarkdownRenderer>();
        Services.AddSingleton<TimeProvider>(clock);
        return clock;
    }

    private static DraftBackupRecord Record(string title, string body) =>
        new(title, body,
            SavedStamp: "2026-10-06T11:00:00.0000000Z",
            Time: "2026-10-06T11:30:00.0000000Z",
            LocalTime: "10/6/2026, 1:30:00 PM");

    private Guid LoadAPost()
    {
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "Loaded title", null, "Loaded body", null, null,
            PostStatus.Draft, null, Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1,
            ContentEditedAt: LoadedContentEdit);
        return id;
    }

    [Fact]
    public async Task EditorLoad_ReadsTheLocalBackupThroughTheJsModule()
    {
        Setup();
        var id = LoadAPost();

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogDraft.read"].Should()
            .HaveCount(1, "the editor reads its local draft backup once on load"));
        JSInterop.Invocations["blogDraft.read"][0].Arguments[0].Should().Be(id.ToString(),
            "the read is keyed to the post the editor loaded — a partially rendered DOM must never mis-key it");
    }

    [Fact]
    public async Task EditorLoad_ReadsTheLocalBackupOnlyAfterTheSavedTextHasLoaded()
    {
        // Blazor renders the page while OnInitializedAsync is still fetching the post (it
        // renders at the first await's yield). The backup must not be read until the saved text
        // is in the model: read too early the editor compares against nothing, keyed to "new",
        // and the backup under the post id is silently ignored.
        Setup();
        var id = LoadAPost();
        _api.PostByIdGate = new TaskCompletionSource();

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("post-edit"));
        JSInterop.VerifyNotInvoke("blogDraft.read",
            "the backup is read only once the saved text it is compared against has loaded");

        _api.PostByIdGate.SetResult();
        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogDraft.read"].Should()
            .HaveCount(1, "once loaded, the editor reads its local draft backup"));
    }

    [Fact]
    public async Task BackupDifferingFromTheSavedText_ShowsThePromptWithItsLocalTime()
    {
        Setup();
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true)
            .SetResult(Record("Typed title", "Typed body."));

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => cut.Find(".draft-restore__text").TextContent.Trim().Should().Be(
            "You have unsaved changes from 10/6/2026, 1:30:00 PM. Restore them?",
            "the prompt names the backup's local date and time and asks the one question"));

        // Never a silent replace: before the user chooses, the editor keeps the saved text.
        cut.FindComponent<MarkdownEditor>().Instance.Value.Should().Be("Loaded body");
    }

    [Fact]
    public async Task Restore_PutsTheBackupTitleAndBodyInTheForm_AndKeepsTheBackup()
    {
        Setup();
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true)
            .SetResult(Record("Typed title", "Typed body."));
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        await cut.WaitForAssertionAsync(() => cut.Find(".draft-restore__restore"));

        cut.Find(".draft-restore__restore").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() =>
            cut.FindComponent<MarkdownEditor>().Instance.Value.Should().Be("Typed body.",
                "Restore puts the backup's body in the editor"));
        cut.FindComponents<MudTextField<string>>()[0].Instance.GetState(x => x.Value).Should().Be("Typed title",
            "Restore puts the backup's title in the editor");
        cut.FindAll(".draft-restore").Should().BeEmpty("the prompt closes after the choice");

        // Unsaved, so the user saves: the backup is not touched.
        JSInterop.VerifyNotInvoke("blogDraft.remove", "Restore never deletes the backup");
        JSInterop.VerifyNotInvoke("blogDraft.saved", "Restore never claims a save");
    }

    [Fact]
    public async Task Discard_RemovesTheBackup_AndKeepsTheLoadedText()
    {
        Setup();
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true)
            .SetResult(Record("Typed title", "Typed body."));
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        await cut.WaitForAssertionAsync(() => cut.Find(".draft-restore__discard"));

        cut.Find(".draft-restore__discard").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogDraft.remove"].Should()
            .HaveCount(1, "Discard deletes the backup"));
        JSInterop.Invocations["blogDraft.remove"][0].Arguments[0].Should().Be(id.ToString(),
            "the delete is keyed to the post the editor loaded, like the read");
        cut.FindAll(".draft-restore").Should().BeEmpty();
        cut.FindComponent<MarkdownEditor>().Instance.Value.Should().Be("Loaded body",
            "Discard keeps the saved text in the editor");
    }

    [Fact]
    public async Task BackupIdenticalToTheSavedText_IsRemovedSilently_WithNoPrompt()
    {
        Setup();
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true)
            .SetResult(Record("Loaded title", "Loaded body"));

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogDraft.remove"].Should()
            .HaveCount(1, "an identical backup is the brief's only silent delete"));
        cut.FindAll(".draft-restore").Should().BeEmpty("an identical backup offers nothing to restore");
    }

    [Fact]
    public async Task SuccessfulSave_ClearsTheBackupThroughTheJsModule()
    {
        Setup();
        var id = LoadAPost();
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogDraft.saved"].Should()
            .HaveCount(1, "a successful save clears the backup"));
        JSInterop.Invocations["blogDraft.saved"][0].Arguments[0].Should().Be(id.ToString(),
            "the cleared backup is the post's; the module keys future backups to the id");
    }

    [Fact]
    public async Task FailedSave_KeepsTheBackup()
    {
        Setup();
        var id = LoadAPost();
        _api.PostFailure = new HttpRequestException("the API refused the save");
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        cut.FindAll("button").Single(b => b.TextContent == "Save draft").Click(new MouseEventArgs());

        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit__error").TextContent.Should()
            .Contain("refused the save"));
        JSInterop.VerifyNotInvoke("blogDraft.saved", "only a successful save clears the backup");
    }

    [Fact]
    public void TheForm_ReceivesThePostKeyAndTheSavedVersionStampTheEditorLoaded()
    {
        Setup();
        var id = LoadAPost();

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.DraftKey.Should().Be(id.ToString(), "an edit route backs up under the post id");
        form.SavedStamp.Should().Be("2026-10-06T11:00:00.0000000Z",
            "the stamp is the server's edited stamp, falling back to updatedAt");
    }

    [Fact]
    public void ANewPostForm_BackupsUnderNew_WithNoSavedVersionStamp()
    {
        Setup();

        var cut = Render<PostEditPage>();

        var form = cut.FindComponent<PostEditorForm>().Instance;
        form.DraftKey.Should().Be("new");
        form.SavedStamp.Should().Be(string.Empty, "a new post has no saved version yet");
    }
}
