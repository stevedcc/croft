using Croft.Api.Services;
using Croft.Api.Tests.Fixtures;
using Croft.Shared.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Croft.Api.Tests;

public class ExceptionMappingTests(PostgresDatabaseFixture fixture)
    : IClassFixture<PostgresDatabaseFixture>
{
    private const string SecretMessage = "category lookup exploded 3f9a2b";

    // Logging is attributed per logger category; the handler's own category is how the test
    // tells the handler's log apart from anything else that might log the same exception.
    private const string ApiExceptionHandlerLogCategory = "Croft.Api.Errors.ApiExceptionHandler";

    private static readonly Dictionary<string, string?> AnalyticsConfig = new()
    {
        ["Analytics:HashKey"] = "test-hash-key",
        ["Analytics:IngestKey"] = "test-ingest-key",
        // Outside Development the API refuses to start without an audience, so Production
        // runs of these tests need one. The assertion on behaviour is unchanged.
        ["Oidc:Audience"] = "test-audience"
    };

    [Fact]
    public async Task ValidationException_ReturnsDomainMessage()
    {
        await using var factory = CreateFactoryAsync();
        var admin = factory.CreateClient().WithRole("Admin");

        var response = await admin.PostAsJsonAsync("/api/posts", new CreatePostRequest("", "content"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Detail.Should().Be("Title is required.");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnexpectedException_Returns500ProblemDetails_WithoutExceptionDetail(string environment)
    {
        await using var factory = (CreateFactoryAsync(
                AnalyticsConfig,
                services => services.AddSingleton<ICategoryService>(new ThrowingCategoryService())))
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/categories");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "an exception the app does not map is a 500");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        var problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonSerializerOptions.Web);
        problem!.Status.Should().Be(500);
        problem.Title.Should().Be("An error occurred");
        problem.Detail.Should().Be("An unexpected error occurred.");
        body.Should().NotContain(SecretMessage, "the exception message must not leak into the response");
        body.Should().NotContain("   at ", "a stack trace must not leak into the response");
    }

    [Fact]
    public async Task ApiExceptionHandler_LogsHandledExceptionAsError()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = CreateFactoryAsync(
            configureServices: services =>
            {
                services.AddSingleton<ICategoryService>(new ThrowingCategoryService());
                services.AddLogging(logging => logging.AddProvider(logs));
            });
        var client = factory.CreateClient();

        await client.GetAsync("/api/categories");

        logs.Entries.Should().Contain(
            entry => entry.Category == ApiExceptionHandlerLogCategory
                     && entry.Level == LogLevel.Error
                     && entry.Exception != null
                     && entry.Exception.Message == SecretMessage,
            "the exception handler must log the exceptions it handles itself; the framework no longer logs handled exceptions");
    }

    private BlogApiFactory CreateFactoryAsync(
        IReadOnlyDictionary<string, string?>? configuration = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var storagePath = Path.Combine(Path.GetTempPath(), $"blog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storagePath);
        // The class fixture has already created and migrated the class database.
        return new BlogApiFactory(fixture.ConnectionString, storagePath, configuration, configureServices);
    }

    private sealed class ThrowingCategoryService : ICategoryService
    {
        public Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
        public Task<PagedResult<CategoryDto>> ListAsync(int page, int pageSize, string? q, CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
        public Task<IReadOnlyList<CategoryDto>> TopAsync(int count, CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
        public Task<CategoryDto?> GetBySlugAsync(string slug, CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
        public Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
        public Task<CategoryDto?> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException(SecretMessage);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((categoryName, logLevel, exception));
        }
    }
}
