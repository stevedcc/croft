using System.Reflection;
using Croft.Api.Data;
using Croft.Api.Models;
using Croft.Api.Services.ContentExport;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace Croft.Api.Tests;

/// <summary>
/// The completeness guard for the content export (the same idea as the draft backup v2 guard):
/// every public settable property of the content entities is either exported or on the
/// documented exclusion list, so a new field can never be silently missed.
/// </summary>
public class ContentExportCompletenessTests
{
    public static TheoryData<Type> Entities() => [.. ContentFormat.Records.Keys];

    /// <summary>
    /// Every column and relation the EF model knows for the entity, shadow properties included
    /// (a shadow property has no CLR property, so a reflection walk cannot see it). The model is
    /// built without connecting to a database.
    /// </summary>
    private static IEnumerable<string> ModelMembers(Type entity)
    {
        using var db = new BlogDbContext(new DbContextOptionsBuilder<BlogDbContext>().UseNpgsql("Host=model-only;Database=model-only").Options);
        var entityType = db.Model.FindEntityType(entity)!;
        return
        [
            .. entityType.GetProperties().Select(property => property.Name),
            .. entityType.GetNavigations().Select(navigation => navigation.Name),
            .. entityType.GetSkipNavigations().Select(navigation => navigation.Name)
        ];
    }

    private static IEnumerable<string> SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name);

    private static HashSet<string> Exported(Type record) =>
    [
        .. record.GetProperties().Select(property => property.Name),
        .. record.GetProperties().SelectMany(property => property.GetCustomAttributes<CoversAttribute>()).Select(covers => covers.EntityProperty)
    ];

    private static IReadOnlyDictionary<string, string> ExclusionsOf(Type entity) =>
        ContentFormat.Exclusions.TryGetValue(entity, out var exclusions) ? exclusions : new Dictionary<string, string>();

    [Theory]
    [MemberData(nameof(Entities))]
    public void EveryPublicSettableProperty_IsExportedOrExcludedWithAReason(Type entity)
    {
        var exported = Exported(ContentFormat.Records[entity]);
        var exclusions = ExclusionsOf(entity);

        var missed = SettableProperties(entity).Where(name => !exported.Contains(name) && !exclusions.ContainsKey(name)).ToList();

        missed.Should().BeEmpty(
            "{0} has properties the export neither writes nor excludes; add them to the export record, or to "
            + "ContentFormat.Exclusions with the reason", entity.Name);
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public void EveryColumnAndRelationInTheEfModel_IsExportedOrExcludedWithAReason(Type entity)
    {
        var exported = Exported(ContentFormat.Records[entity]);
        var exclusions = ExclusionsOf(entity);

        var missed = ModelMembers(entity).Where(name => !exported.Contains(name) && !exclusions.ContainsKey(name)).ToList();

        missed.Should().BeEmpty(
            "the EF model of {0} has columns or relations (shadow properties included) the export neither writes "
            + "nor excludes; export them, or add them to ContentFormat.Exclusions with the reason", entity.Name);
    }

    [Theory]
    [MemberData(nameof(Entities))]
    public void TheExportAndTheExclusionList_NameOnlyPropertiesThatExist(Type entity)
    {
        var real = SettableProperties(entity).Concat(ModelMembers(entity)).ToHashSet();
        var covered = ContentFormat.Records[entity].GetProperties()
            .SelectMany(property => property.GetCustomAttributes<CoversAttribute>()).Select(covers => covers.EntityProperty);

        covered.Where(name => !real.Contains(name)).Should().BeEmpty("a Covers attribute must name an entity property");
        ExclusionsOf(entity).Keys.Where(name => !real.Contains(name)).Should().BeEmpty("an exclusion must name an entity property");
        ExclusionsOf(entity).Keys.Where(Exported(ContentFormat.Records[entity]).Contains).Should().BeEmpty("a property is exported or excluded, not both");
        ExclusionsOf(entity).Values.Should().OnlyContain(reason => !string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Entities_CoverEveryEntityWithContent()
    {
        ContentFormat.Records.Keys.Should().BeEquivalentTo([typeof(Post), typeof(Category), typeof(Tag), typeof(MediaAsset), typeof(Feedback)]);
    }
}
