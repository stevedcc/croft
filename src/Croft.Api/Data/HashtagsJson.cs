// WORKAROUND-FILE(W-102, EF Core): a nullable jsonb column for a list, since a complex collection is NOT NULL and a converter never sees NULL
using System.Text.Json;
using Croft.Shared.Contracts.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Croft.Api.Data;

/// <summary>
/// Maps a hashtag list to one nullable <c>jsonb</c> column: an empty list is stored as NULL and NULL reads as an empty list, so the migration
/// only adds a nullable column (a pod of the previous release never reads it). EF does not run a converter on NULL, so the entities' setters coalesce null to empty.
/// </summary>
internal static class HashtagsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static PropertyBuilder<List<HashtagDto>> HasHashtagsColumn(this PropertyBuilder<List<HashtagDto>> property)
    {
        var converter = new ValueConverter<List<HashtagDto>, string?>(
            list => list.Count == 0 ? null : JsonSerializer.Serialize(list, Options),
            json => JsonSerializer.Deserialize<List<HashtagDto>>(json!, Options) ?? new List<HashtagDto>());
        var comparer = new ValueComparer<List<HashtagDto>>(
            (a, b) => a!.SequenceEqual(b!),
            list => list.Aggregate(0, (hash, h) => HashCode.Combine(hash, h)),
            list => list.ToList());

        // A NULL column is never converted: the entity's setter turns the null EF hands it into an empty list.
        return property.HasColumnType("jsonb").IsRequired(false).HasConversion(converter, comparer).UsePropertyAccessMode(PropertyAccessMode.Property);
    }
}
