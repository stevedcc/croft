using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Croft.Api.Services;

public partial class SlugService : ISlugService
{
    /// <summary>What <see cref="GenerateSlug"/> answers for a title with nothing a slug can carry.</summary>
    public const string FallbackSlug = "untitled";

    public string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return FallbackSlug;

        var normalized = title.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();

        foreach (var c in normalized)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        var slug = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        slug = slug.Replace("#", "-sharp-").Replace("+", "-plus-");
        slug = WhitespaceOrNonWord().Replace(slug, "-").Trim('-');

        if (string.IsNullOrEmpty(slug))
            return FallbackSlug;

        const int maxLength = 100;
        if (slug.Length > maxLength)
            slug = slug[..maxLength].Trim('-');

        return slug;
    }

    public async Task<string> GenerateUniqueSlugAsync(string title, Func<string, Task<bool>> existsAsync)
    {
        var baseSlug = GenerateSlug(title);
        var slug = baseSlug;
        var counter = 1;

        while (await existsAsync(slug))
        {
            slug = $"{baseSlug}-{counter}";
            counter++;
        }

        return slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex WhitespaceOrNonWord();
}
