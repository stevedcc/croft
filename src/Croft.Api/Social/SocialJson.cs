using System.Text.Json;

namespace Croft.Api.Social;

/// <summary>Tolerant reads of a platform's JSON: a missing or mistyped field is "absent", never an exception.</summary>
internal static class SocialJson
{
    public static readonly JsonDocumentOptions Options = new() { MaxDepth = 128 };

    public static JsonDocument Parse(string json, string what)
    {
        try
        {
            return JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new SocialParseException($"The {what} answer is not valid JSON.", ex);
        }
    }

    /// <summary>The property, or a default (<c>Undefined</c>) element when <paramref name="element"/> is no object or has none.</summary>
    public static JsonElement Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? Str(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static bool IsTrue(this JsonElement element, string name) => element.Prop(name).ValueKind == JsonValueKind.True;

    public static bool IsObject(this JsonElement element) => element.ValueKind == JsonValueKind.Object;

    public static IEnumerable<JsonElement> Items(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];
}
