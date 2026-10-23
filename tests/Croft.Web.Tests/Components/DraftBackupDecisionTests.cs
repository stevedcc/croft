using Croft.Web.Components.Admin;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The draft-backup decision on editor load (D3, owner's decision 2 — a prompt, never a silent
/// replace): a backup identical to the saved text is the brief's ONLY silent delete; every
/// differing backup is prompted with its time, including one older than the current server save
/// (a silently dropped backup loses words).
/// </summary>
public class DraftBackupDecisionTests
{
    private static DraftBackupRecord Backup(string title, string body, string time = "2026-10-12T10:00:00Z") =>
        new(title, body, SavedStamp: "2026-10-12T09:00:00Z", Time: time, LocalTime: "10/12/2026, 10:00:00 AM");

    [Fact]
    public void BackupIdenticalToTheSavedText_IsDeletedSilently()
    {
        DraftBackup.Decide("A title", "Some body.", Backup("A title", "Some body."))
            .Should().Be(DraftBackupAction.DeleteSilently);
    }

    [Fact]
    public void BackupIdenticalToTheSavedText_IsDeletedSilently_EvenWhenItIsNewerThanTheSave()
    {
        DraftBackup.Decide("A title", "Some body.", Backup("A title", "Some body.", time: "2026-10-12T11:00:00Z"))
            .Should().Be(DraftBackupAction.DeleteSilently);
    }

    [Fact]
    public void AnEmptyBackupOnAnEmptyEditor_IsDeletedSilently()
    {
        // The new-post editor loads with nothing saved: a backup of nothing is nothing to restore.
        DraftBackup.Decide(string.Empty, string.Empty, Backup(string.Empty, string.Empty))
            .Should().Be(DraftBackupAction.DeleteSilently);
    }

    [Fact]
    public void BackupWithADifferentBody_IsPrompted()
    {
        DraftBackup.Decide("A title", "Some body.", Backup("A title", "Typed something else."))
            .Should().Be(DraftBackupAction.Prompt);
    }

    [Fact]
    public void BackupWithADifferentTitle_IsPrompted()
    {
        DraftBackup.Decide("A title", "Some body.", Backup("A typed title", "Some body."))
            .Should().Be(DraftBackupAction.Prompt);
    }

    [Fact]
    public void BackupOlderThanTheCurrentServerSave_IsStillPrompted_NeverDroppedSilently()
    {
        // The ruling's corner: a backup from before someone saved newer text on another machine.
        // Owner's decision 2 — a prompt, never a silent replace: the words may still be wanted.
        var older = Backup("A title", "Words typed before the other save.", time: "2026-10-12T08:00:00Z");

        DraftBackup.Decide("A title", "The newer saved body.", older).Should().Be(DraftBackupAction.Prompt);
    }
}
