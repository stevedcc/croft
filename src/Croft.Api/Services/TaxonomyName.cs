using Croft.Api.Errors;

namespace Croft.Api.Services;

/// <summary>
/// The rules for a category or tag name, shared by both services on create and update so a bad
/// name is a 400 with a message, never a database error and a 500: required, at most the column's
/// length, and able to carry a slug.
/// </summary>
internal static class TaxonomyName
{
    /// <summary>The column length (<c>HasMaxLength(128)</c> in the entity configurations).</summary>
    public const int MaxLength = 128;

    public static void Validate(string? name, ISlugService slugService)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationException("Name is required.");

        if (name.Length > MaxLength)
            throw new ValidationException($"Name must be {MaxLength} characters or fewer.");

        // The generator answers its fallback for a name with no letter or digit it can carry
        // ("!!!", a name in a script it cannot transliterate). Creating is idempotent by slug, so
        // such a name would silently return the one row that already has the fallback slug.
        if (slugService.GenerateSlug(name) == SlugService.FallbackSlug
            && !name.Trim().Equals(SlugService.FallbackSlug, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("The name needs at least one Latin letter or digit (a-z, 0-9) to make its address.");
    }
}
