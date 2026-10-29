namespace Croft.Web.E2E;

/// <summary>
/// The one IdP address rule shared by start-local.sh, dev/run-e2e.sh and the E2E helpers: the
/// mock IdP's address is Oidc__Authority — the environment when it is set and non-empty — else
/// the default the owner's .env.example carries. Checked on the pure seam: the fixtures run on
/// parallel NUnit workers that read the same process environment, so no test here may change it.
/// </summary>
public class OAuthAuthorityResolutionTests
{
    [Test]
    public void ResolveOAuthAuthority_SetAndNonEmpty_ReturnsItUnchanged() =>
        Assert.That(WebTest.ResolveOAuthAuthority("http://idp:8080/default"),
            Is.EqualTo("http://idp:8080/default"),
            "Oidc__Authority set in the environment is the mock IdP's address");

    [Test]
    public void ResolveOAuthAuthority_Null_ReturnsTheDefault() =>
        Assert.That(WebTest.ResolveOAuthAuthority(null), Is.EqualTo("http://localhost:5556/default"),
            "without Oidc__Authority the address is the .env.example default");

    [Test]
    public void ResolveOAuthAuthority_Empty_ReturnsTheDefault() =>
        Assert.That(WebTest.ResolveOAuthAuthority(""), Is.EqualTo("http://localhost:5556/default"),
            "an empty variable is missing, exactly like in the shell rule");

    [Test]
    public void ResolveOAuthAuthority_WhitespaceOnly_CountsAsSetAndPassesThroughUnchanged() =>
        Assert.That(WebTest.ResolveOAuthAuthority("   "), Is.EqualTo("   "),
            "whitespace-only is set and non-empty, as in the shell's [ -n ] test");
}
