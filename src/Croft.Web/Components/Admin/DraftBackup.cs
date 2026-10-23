using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;

namespace Croft.Web.Components.Admin;

/// <summary>
/// The editor's local draft backup as the draft-backup.js module stores it in localStorage under
/// <c>croft-draft:&lt;post id&gt;</c> (D3's guaranteed layer). A version 2 record carries every
/// field the form edits: the four texts (title, subtitle, search and link-preview description,
/// Markdown body), the category, the tags in order, the banner with its URL and alt text for the
/// thumbnail, the publish date and time, and the page-only navigation fields. A version 1 record
/// (no <c>version</c>) carries title and body only. SavedStamp is the server's saved-version
/// stamp the editor loaded; Time is the backup's write instant; LocalTime is that instant in the
/// browser's local date and time — the module formats it, so the prompt speaks the reader's clock.
/// </summary>
public sealed record DraftBackupRecord(string Title, string Body, string SavedStamp, string Time, string LocalTime)
{
    public int Version { get; init; } = 1;

    public string Subtitle { get; init; } = string.Empty;

    public string Excerpt { get; init; } = string.Empty;

    public CategoryDto? Category { get; init; }

    public List<string> Tags { get; init; } = [];

    public MediaAssetDto? Banner { get; init; }

    /// <summary>The publish date as <c>yyyy-MM-dd</c>, or null.</summary>
    public string? PublishedDate { get; init; }

    /// <summary>The publish time as a constant-format time span (<c>14:30:00</c>), or null.</summary>
    public string? PublishedTime { get; init; }

    /// <summary>Stored as its name ("Footer"), readable in the browser's localStorage.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<NavigationPlacement>))]
    public NavigationPlacement NavigationPlacement { get; init; }

    public int NavigationOrder { get; init; }
}

/// <summary>What the editor does with a backup found on load (owner's decision 2).</summary>
public enum DraftBackupAction
{
    /// <summary>The brief's only silent delete: the backup is exactly the saved text.</summary>
    DeleteSilently,

    /// <summary>Ask the user — never a silent replace.</summary>
    Prompt,
}

public static class DraftBackup
{
    private const string DateFormat = "yyyy-MM-dd";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The load decision (D3, owner's decision 2 — a prompt, never a silent replace): a backup
    /// identical to the saved text is deleted silently, and every differing backup is prompted
    /// with its time — including one older than the current server save, because a silently
    /// dropped backup loses words.
    /// </summary>
    public static DraftBackupAction Decide(string savedTitle, string savedBody, DraftBackupRecord backup) =>
        backup.Title == savedTitle && backup.Body == savedBody
            ? DraftBackupAction.DeleteSilently
            : DraftBackupAction.Prompt;

    /// <summary>
    /// The load decision for the whole form: a version 2 backup is compared on every field, a
    /// version 1 backup on the title and body it has.
    /// </summary>
    public static DraftBackupAction Decide(PostEditModel saved, DraftBackupRecord backup)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(backup);
        if (Decide(saved.Title, saved.MarkdownContent, backup) == DraftBackupAction.Prompt)
        {
            return DraftBackupAction.Prompt;
        }

        return backup.Version < 2 || SameAsSaved(saved, backup)
            ? DraftBackupAction.DeleteSilently
            : DraftBackupAction.Prompt;
    }

    /// <summary>
    /// The non-text fields as the JSON the form renders into <c>data-draft-extra</c>, for the
    /// browser module to merge into its record next to the live texts (the Markdown body is too
    /// large to render into the page on every edit, so the texts are never part of this).
    /// </summary>
    public static string ExtraJson(PostEditModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return JsonSerializer.Serialize(Extra.Of(model), Web);
    }

    /// <summary>
    /// Puts the backup's fields into the model, unsaved. A version 1 record sets title and body
    /// only; a version 2 record sets every field.
    /// </summary>
    public static void ApplyTo(PostEditModel model, DraftBackupRecord backup)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(backup);
        model.Title = backup.Title;
        model.MarkdownContent = backup.Body;
        if (backup.Version < 2)
        {
            return;
        }

        model.Subtitle = backup.Subtitle;
        model.Excerpt = backup.Excerpt;
        model.Category = backup.Category;
        model.CategoryId = backup.Category?.Id;
        model.Tags = [.. backup.Tags];
        model.Banner = backup.Banner;
        model.BannerImageId = backup.Banner?.Id;
        model.PublishedAtDate = backup.PublishedDate is { } date
            ? DateTime.SpecifyKind(DateTime.ParseExact(date, DateFormat, CultureInfo.InvariantCulture), DateTimeKind.Unspecified)
            : null;
        model.PublishedAtTime = backup.PublishedTime is { } time
            ? TimeSpan.ParseExact(time, "c", CultureInfo.InvariantCulture)
            : null;
        model.NavigationPlacement = backup.NavigationPlacement;
        model.NavigationOrder = backup.NavigationOrder;
    }

    /// <summary>
    /// Whether a version 2 backup holds exactly the saved form. Category and banner are compared
    /// by id: their other fields (a post count) change under the editor without being edits.
    /// </summary>
    private static bool SameAsSaved(PostEditModel saved, DraftBackupRecord backup)
    {
        var savedExtra = Extra.Of(saved);
        return backup.Subtitle == saved.Subtitle
            && backup.Excerpt == saved.Excerpt
            && backup.Category?.Id == saved.Category?.Id
            && backup.Banner?.Id == saved.Banner?.Id
            && backup.Tags.SequenceEqual(saved.Tags)
            && backup.PublishedDate == savedExtra.PublishedDate
            && backup.PublishedTime == savedExtra.PublishedTime
            && backup.NavigationPlacement == saved.NavigationPlacement
            && backup.NavigationOrder == saved.NavigationOrder;
    }

    /// <summary>The non-text fields of the form, in the shape both the attribute and the record carry.</summary>
    private sealed record Extra(
        CategoryDto? Category,
        IReadOnlyList<string> Tags,
        MediaAssetDto? Banner,
        string? PublishedDate,
        string? PublishedTime,
        [property: JsonConverter(typeof(JsonStringEnumConverter<NavigationPlacement>))] NavigationPlacement NavigationPlacement,
        int NavigationOrder)
    {
        public static Extra Of(PostEditModel model) => new(
            model.Category,
            model.Tags,
            model.Banner,
            model.PublishedAtDate?.ToString(DateFormat, CultureInfo.InvariantCulture),
            model.PublishedAtTime?.ToString("c", CultureInfo.InvariantCulture),
            model.NavigationPlacement,
            model.NavigationOrder);
    }
}
