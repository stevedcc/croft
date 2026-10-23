using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Croft.Web.Authentication;
using AwesomeAssertions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Croft.Web.Tests;

public class BackchannelLogoutTokenValidatorTests : IDisposable
{
    private const string Authority = "http://idp.example.com";
    private const string ClientId = "blog-web";
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsa = RSA.Create(2048);

    public void Dispose()
    {
        _rsa.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Validate_ValidTokenWithSid_ReturnsPrincipal()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa);

        var principal = await validator.ValidateAsync(token, CancellationToken.None);

        principal.Should().NotBeNull();
        principal!.FindFirst("sid")!.Value.Should().Be("idp-sid-1");
    }

    [Fact]
    public async Task Validate_ValidTokenWithSubOnly_ReturnsPrincipal()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload.Remove("sid"));

        var principal = await validator.ValidateAsync(token, CancellationToken.None);

        principal.Should().NotBeNull("a logout token with only sub is valid and logs out the whole user");
        principal!.FindFirst("sub")!.Value.Should().Be("admin-id");
    }

    [Fact]
    public async Task Validate_IssuerIsCheckedAgainstTheDiscoveredIssuer_NotAConfiguredString()
    {
        // The IdP announces its issuer in the discovery document; a configured authority that
        // differs only in a trailing slash or the host's casing must not reject its tokens.
        const string Announced = "https://IdP.example.com/realms/blog/";
        var validator = CreateValidator(discoveredIssuer: Announced);

        var accepted = await validator.ValidateAsync(
            CreateToken(_rsa, payload => payload["iss"] = Announced), CancellationToken.None);
        var rejected = await validator.ValidateAsync(
            CreateToken(_rsa, payload => payload["iss"] = Authority), CancellationToken.None);

        accepted.Should().NotBeNull("the token's issuer is exactly the one the IdP's metadata announces");
        rejected.Should().BeNull("an issuer other than the discovered one is refused, whatever was configured");
    }

    [Fact]
    public async Task Validate_DiscoveryWithoutAnIssuer_RejectsEveryToken()
    {
        var validator = CreateValidator(discoveredIssuer: null);

        (await validator.ValidateAsync(CreateToken(_rsa), CancellationToken.None)).Should().BeNull(
            "with no announced issuer there is nothing to compare the token's against");
    }

    [Fact]
    public async Task Validate_TokenSignedByUnknownKey_ReturnsNull()
    {
        using var otherRsa = RSA.Create(2048);
        var validator = CreateValidator();
        var token = CreateToken(otherRsa);

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "the signature must verify against the IdP's signing keys");
    }

    [Fact]
    public async Task Validate_WrongIssuer_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload["iss"] = "http://evil.example.com");

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_WrongAudience_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload["aud"] = "another-client");

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "the aud must be this app's client id");
    }

    [Fact]
    public async Task Validate_MissingIat_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload.Remove("iat"));

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "iat presence and freshness are required");
    }

    [Fact]
    public async Task Validate_StaleIat_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload["iat"] = Now.AddMinutes(-10).ToUnixTimeSeconds());

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "a logout token issued too long ago must be rejected");
    }

    [Fact]
    public async Task Validate_IatTooFarInTheFuture_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload["iat"] = Now.AddMinutes(10).ToUnixTimeSeconds());

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "an iat beyond clock skew means the token is not trustworthy");
    }

    [Fact]
    public async Task Validate_MissingJti_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload.Remove("jti"));

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "jti is required by the back-channel logout spec");
    }

    [Fact]
    public async Task Validate_JtiNotANonEmptyString_ReturnsNull()
    {
        var validator = CreateValidator();
        var emptyJti = CreateToken(_rsa, payload => payload["jti"] = "");
        var numericJti = CreateToken(_rsa, payload => payload["jti"] = 42);

        (await validator.ValidateAsync(emptyJti, CancellationToken.None)).Should().BeNull();
        (await validator.ValidateAsync(numericJti, CancellationToken.None)).Should().BeNull(
            "jti must be a non-empty string");
    }

    [Fact]
    public async Task Validate_MissingEventsClaim_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload.Remove("events"));

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_EventsClaimWithoutLogoutEvent_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa,
            payload => payload["events"] = new Dictionary<string, object> { ["http://schemas.openid.net/event/something-else"] = new Dictionary<string, object>() });

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "the events claim must contain the backchannel-logout event");
    }

    [Fact]
    public async Task Validate_NeitherSidNorSub_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload =>
        {
            payload.Remove("sid");
            payload.Remove("sub");
        });

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Validate_NoncePresent_ReturnsNull()
    {
        var validator = CreateValidator();
        var token = CreateToken(_rsa, payload => payload["nonce"] = "some-nonce");

        (await validator.ValidateAsync(token, CancellationToken.None)).Should().BeNull(
            "a logout token must not contain a nonce");
    }

    [Fact]
    public async Task Validate_UnsignedTokenWithAlgNone_ReturnsNull()
    {
        // The classic bypass: a token that names no algorithm and carries no signature, with every
        // other claim right. A validator that stopped requiring signed tokens (RequireSignedTokens = false)
        // would accept it and end any user's session on request.
        var validator = CreateValidator();
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Authority,
            ["aud"] = ClientId,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = "test-jti-1",
            ["sub"] = "admin-id",
            ["sid"] = "idp-sid-1",
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
            },
        };
        static string Base64Url(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var unsigned = $"{Base64Url("""{"alg":"none","typ":"JWT"}""")}.{Base64Url(JsonSerializer.Serialize(payload))}.";

        (await validator.ValidateAsync(unsigned, CancellationToken.None)).Should().BeNull(
            "an unsigned token is refused: only a token signed with the provider's key ends a session");
        // The same claims, signed, are accepted: the refusal above is about the signature, not the claims.
        (await validator.ValidateAsync(CreateToken(_rsa), CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Validate_NotAToken_ReturnsNull()
    {
        var validator = CreateValidator();

        (await validator.ValidateAsync("not-a-jwt", CancellationToken.None)).Should().BeNull();
        (await validator.ValidateAsync("", CancellationToken.None)).Should().BeNull();
    }

    private BackchannelLogoutTokenValidator CreateValidator(RSA? signingKey = null, string? discoveredIssuer = Authority)
    {
        var key = new RsaSecurityKey(signingKey ?? _rsa) { KeyId = "test-key" };
        var configuration = new OpenIdConnectConfiguration();
        configuration.Issuer = discoveredIssuer;
        configuration.SigningKeys.Add(key);
        return new BackchannelLogoutTokenValidator(
            ClientId,
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            new FakeTimeProvider(Now));
    }

    private static string CreateToken(RSA rsa, Action<Dictionary<string, object>>? configure = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Authority,
            ["aud"] = ClientId,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = "test-jti-1",
            ["sub"] = "admin-id",
            ["sid"] = "idp-sid-1",
            ["events"] = new Dictionary<string, object>
            {
                ["http://schemas.openid.net/event/backchannel-logout"] = new Dictionary<string, object>()
            }
        };
        configure?.Invoke(payload);
        return SignedTestJwt.Create(rsa, "test-key", payload);
    }
}

/// <summary>Signs JWTs with an RSA test key so the validator's signature checks run for real.</summary>
internal static class SignedTestJwt
{
    public static string Create(RSA rsa, string keyId, IDictionary<string, object> payload)
    {
        var header = $$"""{"alg":"RS256","typ":"JWT","kid":"{{keyId}}"}""";
        var unsigned = $"{Base64Url(Encoding.UTF8.GetBytes(header))}.{Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)))}";
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
