using System.Text;

namespace Croft.Web.Tests;

/// <summary>
/// Builds an unsigned JWT-shaped string. Enough for code that only reads the
/// payload (e.g. the <c>exp</c> claim) without validating the signature.
/// </summary>
internal static class TestJwt
{
    public static string Create(DateTimeOffset exp) =>
        Create($$"""{"exp":{{exp.ToUnixTimeSeconds()}}}""");

    private static string Create(string payloadJson) =>
        string.Join(".", Base64Url("""{"alg":"HS256","typ":"JWT"}"""), Base64Url(payloadJson), "sig");

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
