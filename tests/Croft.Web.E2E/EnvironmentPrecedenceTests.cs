namespace Croft.Web.E2E;

/// <summary>
/// The precedence rule shared by start-local.sh, dev/run-e2e.sh and the E2E helpers: a variable
/// already set and non-empty in the environment is never overwritten by .env; .env only fills
/// what is missing. Checked on the pure seam: the fixtures run on parallel NUnit workers that
/// read the same process environment, so no test here may change it.
/// </summary>
public class EnvironmentPrecedenceTests
{
    private const string FromEnvironment = "Host=env;Database=blog;Username=blog;Password=blog";
    private const string FromDotenv = "Host=dotenv;Database=blog;Username=blog;Password=blog";

    [Test]
    public void PreferEnvironment_SetAndNonEmpty_WinsAndSkipsTheDotenvRead()
    {
        var dotenvReads = 0;
        var resolved = WebTest.PreferEnvironment(
            FromEnvironment,
            () =>
            {
                dotenvReads++;
                return FromDotenv;
            });

        Assert.That(resolved, Is.EqualTo(FromEnvironment),
            "a value set and non-empty in the environment is never overwritten by .env");
        Assert.That(dotenvReads, Is.Zero,
            "when the environment supplies the value, .env is not read at all");
    }

    [Test]
    public void PreferEnvironment_Empty_FallsBackToDotenv() =>
        Assert.That(WebTest.PreferEnvironment("", () => FromDotenv), Is.EqualTo(FromDotenv),
            "an empty variable is missing, exactly like in the shell rule");

    [Test]
    public void PreferEnvironment_Unset_FallsBackToDotenv() =>
        Assert.That(WebTest.PreferEnvironment(null, () => FromDotenv), Is.EqualTo(FromDotenv));

    [Test]
    public void PreferEnvironment_ValuesPassThroughUnchanged()
    {
        Assert.That(WebTest.PreferEnvironment("  spaced out  ", () => FromDotenv), Is.EqualTo("  spaced out  "),
            "the winning value is not trimmed");
        Assert.That(WebTest.PreferEnvironment("", () => "  spaced out  "), Is.EqualTo("  spaced out  "),
            "the .env fallback is not trimmed");
        Assert.That(WebTest.PreferEnvironment("   ", () => FromDotenv), Is.EqualTo("   "),
            "whitespace-only is set and non-empty, as in the shell's [ -n ] test");
    }
}
