using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Api.Tests.Fixtures;

/// <summary>
/// A <see cref="BlogApiFactory"/> that runs the API's real JwtBearer pipeline (Program.cs's own
/// token validation parameters and OnTokenValidated) instead of TestAuthHandler, in the
/// environment given. In Production the app validates the audience against Oidc:Audience, so a
/// token with a wrong or missing aud is refused: that is what these factories exist to prove.
/// Only the signing key and issuer are swapped for the test's own (there is no IdP to fetch
/// metadata from); the audience setting is the app's.
/// </summary>
internal static class JwtBearerApi
{
    public static BlogApiFactory Create(
        string connectionString,
        string storagePath,
        string environment = "Production",
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // Production refuses to start without these three (Program.cs).
            ["Oidc:Audience"] = TestTokens.Audience,
            ["Analytics:HashKey"] = "test-hash-key-not-a-secret",
            ["Analytics:IngestKey"] = "test-ingest-key-not-a-secret",
        };
        if (configuration is not null)
        {
            foreach (var (key, value) in configuration)
            {
                settings[key] = value;
            }
        }

        return new BlogApiFactory(
            connectionString,
            storagePath,
            settings,
            configureServices: services =>
            {
                services.Configure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                });
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = string.Empty;
                    options.MetadataAddress = string.Empty;
                    options.TokenValidationParameters.IssuerSigningKeys = [TestTokens.SigningKey];
                    options.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                });
            },
            environment: environment);
    }
}
