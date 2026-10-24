using Croft.Api.Errors;
using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Net;

namespace Croft.Api.Tests;

/// <summary>
/// A 404 is not an error. The API maps ValidationException/NotFoundException/ForbiddenException/
/// ConflictException (and the unique-violation DbUpdateException) to 400/403/404/409 — routine
/// traffic outcomes that must not page the owner on error alerts. Those log at Information with
/// what happened as structured fields and no stack trace; anything unmapped (500) keeps the Error
/// log with the exception. The message says what happened, not "unhandled".
/// </summary>
public class ApiExceptionHandlerLogTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IDisposable
{
    private const string HandlerCategory = "Croft.Api.Errors.ApiExceptionHandler";

    [Fact]
    public async Task ValidationException_400_LogsInformationWithFieldsAndNoException()
    {
        // The real validation path: an empty title is rejected by the service.
        using var factory = CreateFactory(throwOnCategoryCall: null);
        using var client = factory.CreateClient().WithRole("Admin");

        var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertLoggedAsInformation("/api/posts", 400, nameof(ValidationException));
    }

    [Fact]
    public async Task NotFoundException_404_LogsInformationWithFieldsAndNoException()
    {
        using var factory = CreateFactory(new NotFoundException("category not here"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories/nope");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertLoggedAsInformation("/api/categories/nope", 404, nameof(NotFoundException));
    }

    [Fact]
    public async Task ForbiddenException_403_LogsInformationWithFieldsAndNoException()
    {
        using var factory = CreateFactory(new ForbiddenException("not yours"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        AssertLoggedAsInformation("/api/categories", 403, nameof(ForbiddenException));
    }

    [Fact]
    public async Task ConflictException_409_LogsInformationWithFieldsAndNoException()
    {
        using var factory = CreateFactory(new ConflictException("name already used"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertLoggedAsInformation("/api/categories", 409, nameof(ConflictException));
    }

    [Fact]
    public async Task UniqueViolationDbUpdateException_409_LogsInformationWithFieldsAndNoException()
    {
        // The shape the handler matches: a DbUpdateException wrapping a unique-violation
        // PostgresException, exactly what SaveChanges throws on a duplicate slug or name.
        using var factory = CreateFactory(new DbUpdateException(
            "duplicate key", new PostgresException("", "", "", PostgresErrorCodes.UniqueViolation)));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        AssertLoggedAsInformation("/api/categories", 409, nameof(DbUpdateException));
    }

    [Fact]
    public async Task UnmappedException_500_KeepsTheErrorLogWithTheException()
    {
        using var factory = CreateFactory(new InvalidOperationException("category lookup exploded"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        Logs().Where(entry => entry.Category == HandlerCategory).Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Error, "an exception the app does not map is a real error");
        var exception = Logs().Single(entry => entry.Category == HandlerCategory).Exception;
        exception.Should().NotBeNull();
        exception!.Message.Should().Be("category lookup exploded");
        Logs().Should().NotContain(entry => entry.Category == HandlerCategory && entry.Level == LogLevel.Information,
            "a 500 must never be downgraded to a routine Information log");
    }

    public void Dispose()
    {
        _logs.Dispose();
        GC.SuppressFinalize(this);
    }

    private readonly LogCollector _logs = new();

    private IReadOnlyList<LogEntry> Logs() => _logs.Entries;

    private void AssertLoggedAsInformation(string path, int statusCode, string exceptionType)
    {
        var entry = Logs().Single(entry => entry.Category == HandlerCategory);
        entry.Level.Should().Be(LogLevel.Information,
            $"{statusCode} is a routine traffic outcome the owner must never be paged for");
        entry.Exception.Should().BeNull("the stack trace must not travel with a mapped status");
        entry.Message.Should().Be($"Request to {path} ended with {statusCode} ({exceptionType})",
            "the message says what happened, not 'unhandled'");
        entry.Values.Should().Contain(t => t.Key == "Path" && Equals(t.Value, path));
        entry.Values.Should().Contain(t => t.Key == "StatusCode" && Equals(t.Value, statusCode));
        entry.Values.Should().Contain(t => t.Key == "ExceptionType" && Equals(t.Value, exceptionType));
    }

    /// <summary>With a stub category service that throws the given exception on every call
    /// (null: the real services, for the validation path).</summary>
    private BlogApiFactory CreateFactory(Exception? throwOnCategoryCall)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        return new BlogApiFactory(
            fixture.ConnectionString,
            storagePath,
            configureServices: services =>
            {
                if (throwOnCategoryCall is not null)
                {
                    services.AddSingleton<ICategoryService>(new ThrowingCategoryService(throwOnCategoryCall));
                }

                services.AddLogging(logging => logging.AddProvider(_logs));
            });
    }

    private sealed class ThrowingCategoryService(Exception exception) : ICategoryService
    {
        public Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default) => Task.FromException<IReadOnlyList<CategoryDto>>(exception);
        public Task<PagedResult<CategoryDto>> ListAsync(int page, int pageSize, string? q, CancellationToken ct = default) => Task.FromException<PagedResult<CategoryDto>>(exception);
        public Task<IReadOnlyList<CategoryDto>> TopAsync(int count, CancellationToken ct = default) => Task.FromException<IReadOnlyList<CategoryDto>>(exception);
        public Task<CategoryDto?> GetBySlugAsync(string slug, CancellationToken ct = default) => Task.FromException<CategoryDto?>(exception);
        public Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default) => Task.FromException<CategoryDto>(exception);
        public Task<CategoryDto?> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default) => Task.FromException<CategoryDto?>(exception);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromException<bool>(exception);
    }
}
