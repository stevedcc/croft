using Croft.Web.Data;
using Croft.Web.Services;
using Croft.Web.Tests.Fixtures;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Croft.Web.Tests;

/// <summary>
/// "keys import" moves a file key ring's keys into the Web database exactly once, so switching the
/// cluster's key storage signs nobody out: a cookie the old file ring protected must stay readable
/// afterwards. It is idempotent — a key the database already holds is skipped by id — so an
/// interrupted cutover is fixed by simply re-running it.
/// </summary>
[Trait("Category", "Container")]
public class DataProtectionKeyImportTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    [Fact]
    public async Task CookieProtectedWithAFileSystemKeyRing_IsStillReadableAfterKeysImport()
    {
        // The old deployment: one Web instance, its key ring on the file system.
        var keysPath = AuthCookie.CreateKeysDirectory();
        using var fileInstance = CreateFileInstance(keysPath);
        var sessionId = $"s-{Guid.NewGuid():N}";
        var cookie = await AuthCookie.SignInAsync(fileInstance.Services, sessionId);

        using var database = CreateDatabaseInstance();
        var (importExit, _, _) = await RunImportAsync(database.Services, keysPath);

        importExit.Should().Be(0, "the file ring's keys must land in the database");
        await database.Services.GetRequiredService<ITokenStore>().SetAsync(sessionId, new TokenEntry(
            "at-1", "rt-1", DateTimeOffset.UtcNow.AddHours(1), "idt-1", "idp-sid-1", "admin-id"));
        var result = await AuthCookie.AuthenticateAsync(database.Services, cookie);

        result.Succeeded.Should().BeTrue(
            "a cookie the file key ring protected must still be readable after its keys are imported, or the switch signs everybody out (failure: {0})",
            result.Failure?.Message ?? "none");
    }

    [Fact]
    public async Task KeysImport_RunTwice_SecondRunImportsNothing()
    {
        var keysPath = AuthCookie.CreateKeysDirectory();
        using var fileInstance = CreateFileInstance(keysPath);
        await AuthCookie.SignInAsync(fileInstance.Services, "s-1");

        using var database = CreateDatabaseInstance();
        using var scope = database.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CroftWebDbContext>();
        var rowsBefore = await db.DataProtectionKeys.CountAsync();

        var (firstExit, _, _) = await RunImportAsync(database.Services, keysPath);
        var rowsAfterFirst = await db.DataProtectionKeys.CountAsync();
        var (secondExit, _, _) = await RunImportAsync(database.Services, keysPath);

        firstExit.Should().Be(0);
        secondExit.Should().Be(0);
        rowsAfterFirst.Should().Be(rowsBefore + 1, "the first run imports the key exactly once");
        (await db.DataProtectionKeys.CountAsync()).Should().Be(rowsAfterFirst,
            "running the import twice must import nothing the second time");
    }

    [Fact]
    public async Task KeysImport_RunTwice_SecondRunReportsTheKeyAsSkipped()
    {
        var keysPath = AuthCookie.CreateKeysDirectory();
        using var fileInstance = CreateFileInstance(keysPath);
        await AuthCookie.SignInAsync(fileInstance.Services, "s-1");
        var keyName = KeyNameIn(keysPath);

        using var database = CreateDatabaseInstance();
        var (firstExit, firstOutput, _) = await RunImportAsync(database.Services, keysPath);
        var (secondExit, secondOutput, _) = await RunImportAsync(database.Services, keysPath);

        firstExit.Should().Be(0);
        secondExit.Should().Be(0);
        firstOutput.Should().Contain("Imported 1").And.Contain(keyName,
            "the command must print what it imported, counts and names");
        secondOutput.Should().Contain("Imported 0").And.Contain(keyName,
            "the command must print what it skipped — the key is already in the database — counts and names");
    }

    [Fact]
    public async Task KeysImport_WithoutWebDb_FailsWithAClearMessage()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var (exitCode, _, stderr) = await RunImportAsync(services, AuthCookie.CreateKeysDirectory());

        exitCode.Should().Be(1, "without ConnectionStrings:WebDb there is no database to import into");
        stderr.Should().Contain("WebDb", "the failure must say what is missing");
    }

    [Fact]
    public async Task KeysImport_WithAMissingDirectory_FailsWithAClearMessage()
    {
        using var database = CreateDatabaseInstance();
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}");
        var (exitCode, _, stderr) = await RunImportAsync(database.Services, missing);

        exitCode.Should().Be(1);
        stderr.Should().Contain("directory", "the failure must name what is wrong");
    }

    [Fact]
    public async Task KeysImport_WithoutADirectory_PrintsUsageAndFails()
    {
        using var database = CreateDatabaseInstance();
        var (exitCode, _, stderr) = await RunImportAsync(database.Services);

        exitCode.Should().Be(1, "a command without its argument must not pretend to have run");
        stderr.Should().Contain("usage:", "and must say how to invoke it");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunImportAsync(
        IServiceProvider services, params string[] arguments)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = await KeysImportCommand.RunAsync(
            services, ["keys", "import", .. arguments], output, error);
        return (exitCode, output.ToString(), error.ToString());
    }

    private static string KeyNameIn(string keysPath) =>
        Path.GetFileNameWithoutExtension(Directory.GetFiles(keysPath, "*.xml").Single());

    // The old deployment: one Web instance, its key ring on the file system and no Web database.
    private static WebApplicationFactory<Program> CreateFileInstance(string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("DataProtection:KeysPath", keysPath);
        });

    private WebApplicationFactory<Program> CreateDatabaseInstance() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ApiBaseUrl", "http://blog-api.blog.svc.cluster.local/");
            builder.UseSetting("ConnectionStrings:WebDb", fixture.ConnectionString);
        });
}
