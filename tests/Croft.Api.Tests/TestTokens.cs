using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Api.Tests;

/// <summary>
/// Signs test JWTs for the JwtBearer pipeline, so API tests can run the real
/// Program.cs OnTokenValidated (and its role-group mapping) instead of TestAuthHandler.
/// </summary>
internal static class TestTokens
{
    public const string Issuer = "https://test-issuer.local/";

    /// <summary>The audience the JWT factories configure as Oidc:Audience (the OIDC client id).</summary>
    public const string Audience = "croft-api-tests";

    public static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32));

    /// <summary>A token for <see cref="Audience"/>.</summary>
    public static string Create(params Claim[] claims) => CreateFor(Audience, claims);

    /// <summary>A token for the given audience; <c>null</c> issues one with no aud claim at all.</summary>
    public static string CreateFor(string? audience, params Claim[] claims)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: audience,
            claims: claims,
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(30),
            signingCredentials: new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
