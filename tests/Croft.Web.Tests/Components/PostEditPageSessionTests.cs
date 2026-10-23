using Bunit;
using Bunit.TestDoubles;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using Croft.Web.Components.Pages.Admin;
using Croft.Web.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The post editor when the session ends: it backs up before the sign-in redirect, never lets
/// the exception end the circuit, and after signing in again restores the backup without asking
/// (and says so) — while every other load with a backup keeps the prompt.
/// </summary>
public class PostEditPageSessionTests : BlogBunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private FakeBlogApiClient _api = null!;

    public PostEditPageSessionTests()
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
    }

    private Guid LoadAPost()
    {
        _api = Services.GetRequiredService<FakeBlogApiClient>();
        var id = Guid.CreateVersion7();
        _api.PostsById[id] = new PostDto(id, "a-post", "Loaded title", null, "Loaded body", null, null,
            PostStatus.Draft, null, Now.UtcDateTime, Now.UtcDateTime, "author-id", "Author", null, [], null, 1,
            ContentEditedAt: Now.UtcDateTime);
        return id;
    }

    private static DraftBackupRecord Backup() =>
        new("Typed title", "Typed body.", "2026-10-06T11:00:00.0000000Z", "2026-10-06T11:30:00.0000000Z", "10/6/2026, 1:30:00 PM")
        {
            Version = 2,
            Subtitle = "Typed subtitle",
            Excerpt = "Typed description",
            Tags = ["typed-tag"],
        };

    private void Visit(Guid id, string query)
    {
        Services.GetRequiredService<BunitNavigationManager>().NavigateTo($"/admin/posts/{id}/edit{query}");
    }

    [Fact]
    public async Task ArrivingFromASignIn_WithABackup_RestoresItWithoutAskingAndSaysSo()
    {
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true).SetResult(Backup());
        Visit(id, "?signedBackIn=1");

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit__notice").TextContent.Should()
            .Contain("Your session ended, so you signed in again. Your unsaved changes were restored."));
        cut.FindAll(".draft-restore").Should().BeEmpty("a restore after signing in again does not ask");
        cut.FindComponent<MarkdownEditor>().Instance.Value.Should().Be("Typed body.");
        cut.FindComponents<MudTextField<string>>()[0].Instance.GetState(x => x.Value).Should().Be("Typed title");
        cut.FindComponents<MudTextField<string>>()[1].Instance.GetState(x => x.Value).Should().Be("Typed subtitle");
        Services.GetRequiredService<BunitNavigationManager>().Uri.Should().NotContain("signedBackIn",
            "the one-shot parameter is removed from the address bar after use");
        JSInterop.VerifyNotInvoke("blogDraft.remove", "the restored backup keeps protecting the text until a save");
    }

    [Fact]
    public async Task ArrivingFromASignIn_WithoutABackup_ShowsNoNotice_AndStillRemovesTheParameter()
    {
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true).SetResult(null);
        Visit(id, "?signedBackIn=1");

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => JSInterop.Invocations["blogDraft.read"].Should().HaveCount(1));
        cut.FindAll(".post-edit__notice").Should().BeEmpty();
        Services.GetRequiredService<BunitNavigationManager>().Uri.Should().NotContain("signedBackIn");
    }

    [Fact]
    public async Task AnyOtherLoadWithADifferingBackup_KeepsThePrompt_AndShowsNoNotice()
    {
        var id = LoadAPost();
        JSInterop.Setup<DraftBackupRecord?>("blogDraft.read", _ => true).SetResult(Backup());
        Visit(id, string.Empty);

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => cut.Find(".draft-restore"));
        cut.FindAll(".post-edit__notice").Should().BeEmpty();
        cut.FindComponent<MarkdownEditor>().Instance.Value.Should().Be("Loaded body", "nothing is restored unasked");
    }

    [Fact]
    public async Task TheSessionEndingWhileTheEditorLoads_DoesNotEndTheCircuit()
    {
        var id = LoadAPost();
        _api.PostByIdFailure = new SessionExpiredException();

        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));

        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit__error").TextContent.Should()
            .Contain("The session has expired; sign in again."));
    }

    [Fact]
    public async Task TheSessionEndingOnSave_DoesNotEndTheCircuit_AndKeepsTheTextInTheForm()
    {
        var id = LoadAPost();
        _api.PostFailure = new SessionExpiredException();
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__button"));

        cut.FindAll(".post-actions__button").First(b => b.TextContent == "Save draft").Click();

        await cut.WaitForAssertionAsync(() => cut.Find(".post-edit__error").TextContent.Should()
            .Contain("The session has expired"));
        cut.FindComponent<MarkdownEditor>().Instance.Value.Should().Be("Loaded body");
    }

    [Fact]
    public async Task WhenTheSessionEnds_TheEditorWritesItsBackupNow_ThroughTheJsModule()
    {
        var id = LoadAPost();
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        JSInterop.Setup<bool>("blogDraft.flush").SetResult(true);
        var cut = Render<PostEditPage>(p => p.Add(x => x.Id, id));
        await cut.WaitForAssertionAsync(() => cut.Find(".post-actions__button"));

        var kept = await Services.GetRequiredService<SessionExpiryHooks>().RunAsync();

        kept.Should().BeTrue("the editor reports it kept work to restore");
        JSInterop.Invocations["blogDraft.flush"].Should().HaveCount(1);
    }
}
