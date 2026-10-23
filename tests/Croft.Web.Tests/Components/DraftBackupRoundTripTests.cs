using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Croft.Shared.Contracts;
using Croft.Shared.Enums;
using Croft.Web.Components.Admin;
using AwesomeAssertions;

namespace Croft.Web.Tests.Components;

/// <summary>
/// The editor's draft backup keeps every field the form edits. The browser module
/// (draft-backup.js) writes the record: the four text fields come from the live inputs, the rest
/// from the form's <c>data-draft-extra</c> attribute (<see cref="DraftBackup.ExtraJson"/>). These
/// tests stand in for the module — merging the texts over the extras exactly as it does — and
/// check that restoring the record puts every <see cref="PostEditModel"/> property back, so a
/// property added to the model without a place in the backup fails here, named.
/// </summary>
public class DraftBackupRoundTripTests
{
    /// <summary>
    /// Properties the backup is not expected to carry. Each needs a reason: a computed or
    /// server-owned value, or a field the brief keeps out of the backup (the discussion links).
    /// </summary>
    private static readonly HashSet<string> NotBackedUp =
    [
        // Brief 12c: the discussion links are small, pasted from elsewhere and validated by the API on save; the brief keeps them out of the backup.
        nameof(PostEditModel.MastodonUrl),
        nameof(PostEditModel.BlueskyUrl),
        // The API's last refusal of a link: transient display state, not something the user typed.
        nameof(PostEditModel.MastodonUrlError),
        nameof(PostEditModel.BlueskyUrlError),
        // Brief 12d: the announcement text and flags are short, set once per post, and the API checks the text on save: like the links they stay out of the backup.
        nameof(PostEditModel.AnnouncementText),
        nameof(PostEditModel.AnnounceMastodon),
        nameof(PostEditModel.AnnounceBluesky),
        nameof(PostEditModel.AnnouncementTextError),
    ];

    /// <summary>
    /// The model's text properties and the record key the browser module reads each from the live
    /// input (the module's <c>text('key')</c> over the form's <c>data-draft-field="key"</c>).
    /// </summary>
    private static readonly Dictionary<string, string> TextFields = new()
    {
        [nameof(PostEditModel.Title)] = "title",
        [nameof(PostEditModel.Subtitle)] = "subtitle",
        [nameof(PostEditModel.Excerpt)] = "excerpt",
        [nameof(PostEditModel.MarkdownContent)] = "body",
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void EveryFieldOfTheModel_RoundTripsThroughTheBackupRecord()
    {
        var filled = FilledModel();

        var restored = new PostEditModel();
        DraftBackup.ApplyTo(restored, RecordAsTheModuleWritesIt(filled));

        foreach (var property in SettableProperties().Where(p => !NotBackedUp.Contains(p.Name)))
        {
            var expected = property.GetValue(filled);
            var actual = property.GetValue(restored);
            actual.Should().BeEquivalentTo(expected,
                $"PostEditModel.{property.Name} must be in the draft backup (add it to DraftBackup and draft-backup.js, " +
                "or list it in NotBackedUp with a reason)");
        }
    }

    [Fact]
    public void EveryTextPropertyOfTheModel_IsReadByTheBrowserModuleFromAFieldTheFormMarks()
    {
        var textProperties = SettableProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name);
        var root = RepoRoot.Find();
        var module = File.ReadAllText(Path.Combine(root, "src", "Croft.Web", "wwwroot", "js", "draft-backup.js"));
        var form = File.ReadAllText(Path.Combine(root, "src", "Croft.Web", "Components", "Admin", "PostEditorForm.razor"));
        var editor = File.ReadAllText(Path.Combine(root, "src", "Croft.Web", "Components", "Admin", "MarkdownEditor.razor"));

        foreach (var property in textProperties.Where(name => !NotBackedUp.Contains(name)))
        {
            TextFields.Should().ContainKey(property,
                $"PostEditModel.{property} is a text field: give it a record key here and in draft-backup.js");
            var key = TextFields[property];
            module.Should().Contain($"{key}: text('{key}')",
                $"draft-backup.js must write PostEditModel.{property} into the record as '{key}'");
            (form + editor).Should().Contain($"data-draft-field=\"{key}\"",
                $"the form must mark the input of PostEditModel.{property} for the module");
        }
    }

    [Fact]
    public void EveryPropertyOfTheModel_IsFilledByTheTestWithAValueDifferentFromItsDefault()
    {
        var filled = FilledModel();
        var blank = new PostEditModel();

        foreach (var property in SettableProperties())
        {
            property.GetValue(filled).Should().NotBeEquivalentTo(property.GetValue(blank),
                $"PostEditModel.{property.Name} needs a non-default value here, or the round trip proves nothing for it");
        }
    }

    [Fact]
    public void Banner_RoundTripsWithItsUrlAndAltText()
    {
        var filled = FilledModel();

        var restored = new PostEditModel();
        DraftBackup.ApplyTo(restored, RecordAsTheModuleWritesIt(filled));

        restored.BannerImageId.Should().Be(filled.Banner!.Id);
        restored.Banner!.Url.Should().Be(filled.Banner.Url, "the thumbnail's src");
        restored.Banner.AltText.Should().Be(filled.Banner.AltText);
    }

    [Fact]
    public void VersionOneRecord_RestoresTitleAndBodyOnly()
    {
        var record = new DraftBackupRecord("Old title", "Old body", "stamp", "2026-10-12T10:00:00Z", "local");
        var model = new PostEditModel { Subtitle = "Kept subtitle", Excerpt = "Kept excerpt", Tags = ["kept"] };

        DraftBackup.ApplyTo(model, record);

        model.Title.Should().Be("Old title");
        model.MarkdownContent.Should().Be("Old body");
        model.Subtitle.Should().Be("Kept subtitle", "a version 1 record knows nothing of the other fields");
        model.Excerpt.Should().Be("Kept excerpt");
        model.Tags.Should().Equal("kept");
    }

    [Fact]
    public void VersionTwoRecord_DiffersFromTheSavedModel_WhenOnlyANonTextFieldDiffers()
    {
        var saved = FilledModel();
        var identical = RecordAsTheModuleWritesIt(saved);
        DraftBackup.Decide(saved, identical).Should().Be(DraftBackupAction.DeleteSilently);

        var changedTag = RecordAsTheModuleWritesIt(Copy(saved, tags: [.. saved.Tags, "extra"]));
        DraftBackup.Decide(saved, changedTag).Should().Be(DraftBackupAction.Prompt,
            "a backup that differs only in its tags is still unsaved work");
    }

    [Fact]
    public void VersionOneRecord_IsComparedOnTitleAndBodyOnly()
    {
        var saved = FilledModel();
        var sameText = new DraftBackupRecord(saved.Title, saved.MarkdownContent, "s", "t", "l");

        DraftBackup.Decide(saved, sameText).Should().Be(DraftBackupAction.DeleteSilently);
    }

    [Fact]
    public void ThePlacement_IsStoredAsItsName_AndARecordWithFooterRestoresFooter()
    {
        var model = new PostEditModel { NavigationPlacement = NavigationPlacement.Footer };

        JsonNode.Parse(DraftBackup.ExtraJson(model))!["navigationPlacement"]!.GetValue<string>()
            .Should().Be("Footer", "the backup keeps the readable name, not an enum number");

        var record = JsonSerializer.Deserialize<DraftBackupRecord>(
            """{"version":2,"title":"T","body":"B","savedStamp":"s","time":"t","localTime":"l","navigationPlacement":"Footer","tags":[]}""",
            Web)!;
        var restored = new PostEditModel();
        DraftBackup.ApplyTo(restored, record);
        restored.NavigationPlacement.Should().Be(NavigationPlacement.Footer);
    }

    private static DraftBackupRecord RecordAsTheModuleWritesIt(PostEditModel model)
    {
        var record = JsonNode.Parse(DraftBackup.ExtraJson(model))!.AsObject();
        record["version"] = 2;
        record["title"] = model.Title;
        record["subtitle"] = model.Subtitle;
        record["excerpt"] = model.Excerpt;
        record["body"] = model.MarkdownContent;
        record["savedStamp"] = "stamp";
        record["time"] = "2026-10-12T10:00:00Z";
        record["localTime"] = "local";
        return JsonSerializer.Deserialize<DraftBackupRecord>(record.ToJsonString(), Web)!;
    }

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(PostEditModel).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod?.IsPublic == true);

    private static PostEditModel Copy(PostEditModel from, List<string> tags)
    {
        var copy = FilledModel();
        foreach (var property in SettableProperties())
        {
            property.SetValue(copy, property.GetValue(from));
        }

        copy.Tags = tags;
        return copy;
    }

    /// <summary>Every property gets a non-default value; an unknown property type fails loudly.</summary>
    private static PostEditModel FilledModel()
    {
        var model = new PostEditModel();
        foreach (var property in SettableProperties())
        {
            property.SetValue(model, ValueFor(property));
        }

        model.CategoryId = model.Category!.Id;
        model.BannerImageId = model.Banner!.Id;
        return model;
    }

    private static object ValueFor(PropertyInfo property) => property.PropertyType switch
    {
        var t when t == typeof(string) => $"{property.Name} value ✓ \"quoted\"",
        var t when t == typeof(Guid?) => Guid.Parse("11111111-2222-3333-4444-555555555555"),
        var t when t == typeof(CategoryDto) => new CategoryDto(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), "Cat", "cat", "icon", 3),
        var t when t == typeof(List<string>) => new List<string> { "second-tag", "first-tag" },
        var t when t == typeof(MediaAssetDto) => new MediaAssetDto(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"),
            "file.png", "Original.png", "image/png", "/api/media/bbbbbbbb-0000-0000-0000-000000000002",
            MediaAssetScope.PostSpecific, Guid.Parse("cccccccc-0000-0000-0000-000000000003"), "An alt text"),
        var t when t == typeof(DateTime?) => new DateTime(2026, 11, 3),
        var t when t == typeof(TimeSpan?) => new TimeSpan(14, 30, 0),
        // The opposite of the model's own default (a flag may default to true: brief 12d), so the value always differs from a blank model's.
        var t when t == typeof(bool) => !(bool)property.GetValue(new PostEditModel())!,
        var t when t == typeof(NavigationPlacement) => NavigationPlacement.Footer,
        var t when t == typeof(int) => 7,
        _ => throw new InvalidOperationException(
            $"PostEditModel.{property.Name} has the type {property.PropertyType}: teach this test a value for it and make the draft backup carry it.")
    };
}
