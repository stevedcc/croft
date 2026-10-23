# Blog Framework Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a server-side Blazor + MudBlazor personal blog backed by a PostgreSQL/EF Core REST API, with Authentik OAuth2 roles, media uploads, Markdown editing, feedback, search, analytics, scheduling, and Kubernetes deployment manifests.

**Architecture:** A single .NET 8 solution with `Blog.Api` (Web API + EF Core), `Blog.Web` (server-side Blazor + MudBlazor), and `Blog.Shared` (DTOs/enums). The Blazor app authenticates via Authentik OIDC and forwards the access token to the API, which validates JWTs from the same Authentik app.

**Tech Stack:** .NET 10, EF Core + Npgsql.EntityFrameworkCore.PostgreSQL, PostgreSQL 18, MudBlazor, Markdig, Authentik OIDC/JWT, Longhorn-backed PVC for media storage, Docker Compose, plain Kubernetes YAML.

**Spec:** `docs/superpowers/specs/2026-10-01-blog-framework-design.md`

## Global Constraints

- Target framework: **.NET 10**.
- Database access: **EF Core** with `Npgsql.EntityFrameworkCore.PostgreSQL`.
- Database server: **PostgreSQL 18**.
- UI framework: **Server-side Blazor** with **MudBlazor**.
- Auth: **Authentik** via OIDC for `Blog.Web`, JWT bearer for `Blog.Api`.
- Search: **PostgreSQL full-text search (`tsvector`)**.
- Media storage: **Longhorn-backed PVC** mounted at `/app/uploads` in production.
- Local dev database: **Docker Compose**.
- Deployment: **plain Kubernetes YAML manifests**, no Aspire/Helm.
- All secrets/connection strings live in user-secrets, environment variables, or K8s Secrets — never committed.

## Review Focus

The spec implies these behaviors but no single task’s happy-path tests fully exercise them. Each item below is pinned to the task that owns it.

1. **Slug collisions** — two posts with the same title must not overwrite each other. Task 3 tests `PostService.CreateAsync` with duplicate titles and asserts unique slugs.
2. **File path traversal in uploads** — uploaded filenames must not escape the storage root. Task 4 tests `LocalFileStorage.SaveAsync` with a malicious filename and asserts a sanitized path.
3. **Media scope authorization** — a `PostSpecific` asset must be readable only by Admin/Editor when the post is a draft. Task 4 tests `MediaController.Get` for a post-scoped draft asset with anonymous vs. authorized callers.
4. **Scheduled publishing idempotency** — the background job must not repeatedly publish the same post. Task 7 asserts `Status == Published` posts are skipped.
5. **OIDC role claim mapping** — a missing or unrecognized `role` claim must result in no elevated permissions. Task 9 tests the JWT handler with a token that has no `role` claim and asserts 403 on admin endpoints.

---

## Task 1: Solution scaffolding and shared contracts

**Files:**
- Create: `Blog.sln`
- Create: `src/Blog.Shared/Blog.Shared.csproj`
- Create: `src/Blog.Shared/Enums/PostStatus.cs`
- Create: `src/Blog.Shared/Enums/MediaAssetScope.cs`
- Create: `src/Blog.Shared/Contracts/PagedResult.cs`
- Create: `src/Blog.Shared/Contracts/PostListRequest.cs`
- Create: `src/Blog.Shared/Contracts/CreatePostRequest.cs`
- Create: `src/Blog.Shared/Contracts/UpdatePostRequest.cs`
- Create: `src/Blog.Shared/Contracts/PublishRequest.cs`
- Create: `src/Blog.Shared/Contracts/PostDto.cs`
- Create: `src/Blog.Shared/Contracts/TagDto.cs`
- Create: `src/Blog.Shared/Contracts/CategoryDto.cs`
- Create: `src/Blog.Shared/Contracts/MediaAssetDto.cs`
- Create: `src/Blog.Shared/Contracts/FeedbackDto.cs`
- Create: `src/Blog.Shared/Contracts/CreateFeedbackRequest.cs`
- Create: `src/Blog.Shared/Contracts/PageViewRequest.cs`
- Create: `src/Blog.Shared/Contracts/AnalyticsSummaryDto.cs`

**Interfaces:**
- Produces: DTO/enums used by every later task. Property names and types in these records are the contract.

- [ ] **Step 1: Create the solution and shared project**

Create `Blog.sln` referencing `src/Blog.Shared/Blog.Shared.csproj`.

- [ ] **Step 2: Define the shared contracts**

Use C# records/classes with these exact shapes:

```csharp
public enum PostStatus { Draft, Scheduled, Published }
public enum MediaAssetScope { Global, PostSpecific }

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public record PostListRequest(
    string? SearchTerm = null,
    string? TagSlug = null,
    string? CategorySlug = null,
    PostStatus? Status = null,
    int Page = 1,
    int PageSize = 10);

public record CreatePostRequest(
    string Title,
    string MarkdownContent,
    string? Excerpt = null,
    Guid? BannerImageId = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Categories = null);

public record UpdatePostRequest(
    string Title,
    string MarkdownContent,
    string? Excerpt = null,
    Guid? BannerImageId = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Categories = null);

public record PublishRequest(DateTime? PublishedAt = null);

public record PostDto(
    Guid Id,
    string Slug,
    string Title,
    string MarkdownContent,
    string? Excerpt,
    PostStatus Status,
    DateTime? PublishedAt,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string AuthorId,
    string AuthorName,
    MediaAssetDto? BannerImage,
    IReadOnlyList<TagDto> Tags,
    IReadOnlyList<CategoryDto> Categories);

public record TagDto(Guid Id, string Name, string Slug);
public record CategoryDto(Guid Id, string Name, string Slug);

public record MediaAssetDto(
    Guid Id,
    string FileName,
    string OriginalName,
    string ContentType,
    string Url,
    MediaAssetScope Scope,
    Guid? PostId);

public record FeedbackDto(
    Guid Id,
    Guid PostId,
    string AuthorId,
    string AuthorName,
    string QuotedText,
    string Comment,
    DateTime CreatedAt,
    DateTime? ResolvedAt);

public record CreateFeedbackRequest(string QuotedText, string Comment);

public record PageViewRequest(Guid PostId, string? Referrer = null);

public record AnalyticsSummaryDto(
    Guid PostId,
    string Title,
    long TotalViews,
    IReadOnlyList<DailyViewCount> DailyViews);

public record DailyViewCount(DateTime Date, long Count);
```

- [ ] **Step 3: Verify the shared project builds**

Run: `dotnet build src/Blog.Shared/Blog.Shared.csproj`
Expected: build succeeds with no warnings.

- [ ] **Step 4: Commit**

```bash
git add Blog.sln src/Blog.Shared
git commit -m "chore: scaffold solution and shared contracts"
```

---

## Task 2: Domain model and EF Core

**Files:**
- Create: `src/Blog.Api/Blog.Api.csproj`
- Create: `src/Blog.Api/Data/BlogDbContext.cs`
- Create: `src/Blog.Api/Models/Post.cs`
- Create: `src/Blog.Api/Models/Tag.cs`
- Create: `src/Blog.Api/Models/Category.cs`
- Create: `src/Blog.Api/Models/MediaAsset.cs`
- Create: `src/Blog.Api/Models/Feedback.cs`
- Create: `src/Blog.Api/Models/PageView.cs`
- Create: `src/Blog.Api/Data/Configurations/PostConfiguration.cs`
- Create: `tests/Blog.Api.Tests/Blog.Api.Tests.csproj`
- Create: `tests/Blog.Api.Tests/Fixtures/PostgresContainerFixture.cs`

**Interfaces:**
- Consumes: `PostStatus`, `MediaAssetScope` from Task 1.
- Produces: `BlogDbContext` (constructor `DbContextOptions<BlogDbContext>`) and entity classes used by services.

- [ ] **Step 1: Create the API project and reference shared/tests**

`src/Blog.Api/Blog.Api.csproj` references `Blog.Shared`. Test project references `Blog.Api`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, xUnit.

- [ ] **Step 2: Define entities**

```csharp
public class Post
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string MarkdownContent { get; set; } = string.Empty;
    public string? Excerpt { get; set; }
    public PostStatus Status { get; set; }
    public DateTime? PublishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string AuthorId { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public Guid? BannerImageId { get; set; }
    public MediaAsset? BannerImage { get; set; }
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
    public ICollection<MediaAsset> MediaAssets { get; set; } = new List<MediaAsset>();
    public ICollection<Feedback> Feedback { get; set; } = new List<Feedback>();
    public long ViewCount { get; set; }
    public NpgsqlTsVector SearchVector { get; set; } = null!;
}
```

Define `Tag`, `Category` (Name/Slug unique), `MediaAsset` (with `Scope`, `PostId`, `FilePath`, etc.), `Feedback` (QuotedText, Comment, CreatedAt, ResolvedAt), `PageView` (PostId, Timestamp, IpHash, UserAgent, Referrer).

- [ ] **Step 3: Configure the DbContext and full-text search**

In `PostConfiguration.cs`, configure `SearchVector` as a stored generated column:

```csharp
entity.HasGeneratedTsVectorColumn(
    p => p.SearchVector,
    "english",
    p => new { p.Title, p.Excerpt, p.MarkdownContent })
    .HasIndex(p => p.SearchVector)
    .HasMethod("GIN");
```

Configure many-to-many relationships, uniqueness on `Slug`, cascade behavior for post-scoped media.

- [ ] **Step 4: Add the initial EF Core migration**

Run: `dotnet ef migrations add InitialCreate -p src/Blog.Api -s src/Blog.Api`
Expected: `Migrations/InitialCreate.cs` is created.

- [ ] **Step 5: Write an integration test that spins up Postgres and applies migrations**

Use `Testcontainers.PostgreSql` in `PostgresContainerFixture.cs`. Expose a fresh connection string and a `WebApplicationFactory<Blog.Api.Program>` configured to use it.

Test: `BlogDbContextTests.CanApplyMigrations`

```csharp
[Fact]
public async Task CanApplyMigrations()
{
    await using var scope = _factory.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<BlogDbContext>();
    var pending = await db.Database.GetPendingMigrationsAsync();
    pending.Should().BeEmpty();
    (await db.Database.CanConnectAsync()).Should().BeTrue();
}
```

- [ ] **Step 6: Run the test**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj --filter "FullyQualifiedName~CanApplyMigrations"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add EF Core domain model and initial migration"
```

---

## Task 3: Posts, tags, and categories API

**Files:**
- Create: `src/Blog.Api/Services/IPostService.cs`
- Create: `src/Blog.Api/Services/PostService.cs`
- Create: `src/Blog.Api/Services/ISlugService.cs`
- Create: `src/Blog.Api/Services/SlugService.cs`
- Create: `src/Blog.Api/Controllers/PostsController.cs`
- Create: `src/Blog.Api/Controllers/TagsController.cs`
- Create: `src/Blog.Api/Controllers/CategoriesController.cs`
- Create: `tests/Blog.Api.Tests/PostsControllerTests.cs`
- Create: `tests/Blog.Api.Tests/TestAuthHandler.cs`

**Interfaces:**
- Consumes: `BlogDbContext`, `PostDto`, `CreatePostRequest`, etc.
- Produces:
  - `IPostService` with methods `CreateAsync`, `UpdateAsync`, `GetBySlugAsync`, `ListAsync`, `DeleteAsync`, `PublishAsync`.
  - `ISlugService` with `string GenerateSlug(string title, IEnumerable<string> existingSlugs)`.
  - Controllers returning `ActionResult<PostDto>` etc.

- [ ] **Step 1: Write a failing test for slug uniqueness**

```csharp
[Fact]
public async Task CreatePost_WithDuplicateTitle_GeneratesUniqueSlug()
{
    var client = _factory.CreateAdminClient();
    await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Hello World", "content"));
    var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("Hello World", "content2"));
    response.EnsureSuccessStatusCode();
    var post = await response.Content.ReadFromJsonAsync<PostDto>();
    post!.Slug.Should().Be("hello-world-1");
}
```

Run: `dotnet test --filter "FullyQualifiedName~DuplicateTitle"`
Expected: FAIL.

- [ ] **Step 2: Implement `ISlugService` and `PostService`**

`SlugService.GenerateSlug` lowercases, removes non-alphanumeric, trims hyphens, then appends a counter if the slug already exists. `PostService.CreateAsync` saves the post with tags/categories resolved by slug/name.

- [ ] **Step 3: Implement controllers**

`PostsController` with `[Authorize(Roles = "Admin")]` on create/update/delete/publish; `[AllowAnonymous]` on list/get but enforce status filter for anonymous callers. `TagsController` and `CategoriesController` public list, admin create.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add posts, tags, categories endpoints"
```

---

## Task 4: Media upload and storage

**Files:**
- Create: `src/Blog.Api/Services/IFileStorage.cs`
- Create: `src/Blog.Api/Services/LocalFileStorage.cs`
- Create: `src/Blog.Api/Services/IMediaService.cs`
- Create: `src/Blog.Api/Services/MediaService.cs`
- Create: `src/Blog.Api/Controllers/MediaController.cs`
- Create: `tests/Blog.Api.Tests/MediaControllerTests.cs`

**Interfaces:**
- Consumes: `BlogDbContext`, `MediaAsset`, `MediaAssetDto`.
- Produces:
  - `IFileStorage.SaveAsync(string contentType, string originalName, Stream data, CancellationToken)` returns `(string fileName, string relativePath)`.
  - `IMediaService.UploadAsync(IFormFile file, Guid? postId, MediaAssetScope scope, string userId, string userName)` returns `MediaAssetDto`.
  - `MediaController` endpoints: `POST /api/media`, `GET /api/media/{id}`, `GET /api/posts/{id}/media`.

- [ ] **Step 1: Write failing tests for upload and path sanitization**

```csharp
[Fact]
public async Task Upload_MaliciousFilename_IsSanitized()
{
    // arrange a file named "../../../etc/passwd.txt"
    // assert saved file name does not contain ".." or path separators
}

[Fact]
public async Task Get_PostSpecificDraftAsset_RequiresEditor()
{
    // anonymous -> 401/404, editor -> 200
}
```

- [ ] **Step 2: Implement `LocalFileStorage`**

Use `Path.GetFileName`, generate a GUID file name, preserve extension, store under `_basePath/{scope}/{postId?}/{guid}.{ext}`. Reject `..`.

- [ ] **Step 3: Implement `MediaService` and controller**

Persist `MediaAsset`. `GET /api/media/{id}` returns `FileStreamResult` for public/global assets; for `PostSpecific` assets linked to a non-published post, require Admin or Editor role.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj --filter "FullyQualifiedName~Media"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add media upload and retrieval"
```

---

## Task 5: Full-text search

**Files:**
- Create: `src/Blog.Api/Services/ISearchService.cs`
- Create: `src/Blog.Api/Services/SearchService.cs`
- Create: `src/Blog.Api/Controllers/SearchController.cs`
- Create: `tests/Blog.Api.Tests/SearchControllerTests.cs`

**Interfaces:**
- Consumes: `BlogDbContext`, `PostDto`, `SearchVector`.
- Produces: `ISearchService.SearchAsync(string query, int page, int pageSize)` returning `PagedResult<PostDto>` of published posts.

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task Search_ReturnsMatchingPublishedPosts()
{
    // seed a published post containing "kubernetes"
    // search for "kubernetes" -> contains the post
    // search for "azure" -> empty
}
```

- [ ] **Step 2: Implement `SearchService`**

Use `EF.Functions.ToTsQuery("english", query)` against `p.SearchVector`. Filter `Status == Published`. Order by rank (`EF.Functions.ToTsRank`).

- [ ] **Step 3: Add controller and run tests**

`GET /api/search?q={query}` public.

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj --filter "FullyQualifiedName~Search"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add full-text search"
```

---

## Task 6: Analytics

**Files:**
- Create: `src/Blog.Api/Services/IAnalyticsService.cs`
- Create: `src/Blog.Api/Services/AnalyticsService.cs`
- Create: `src/Blog.Api/Controllers/AnalyticsController.cs`
- Create: `tests/Blog.Api.Tests/AnalyticsControllerTests.cs`

**Interfaces:**
- Consumes: `BlogDbContext`, `PageView`, `PageViewRequest`, `AnalyticsSummaryDto`.
- Produces:
  - `IAnalyticsService.RecordPageViewAsync(Guid postId, string ipHash, string? userAgent, string? referrer)`.
  - `IAnalyticsService.GetSummaryAsync(Guid postId, DateTime? from, DateTime? to)`.
  - `POST /api/analytics/pageview`, `GET /api/analytics?postId=...&from=...&to=...`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public async Task RecordPageView_IncrementsPostViewCount()
{
    // record a view -> Post.ViewCount == 1
}

[Fact]
public async Task Summary_GroupsByDay()
{
    // record views on two days -> DailyViews has two entries
}
```

- [ ] **Step 2: Implement service and controller**

Record deduplicated by `IpHash` within a 24-hour window (simpler: unique `(PostId, IpHash, Timestamp::date)`). Increment `Post.ViewCount`. Summary groups by date.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj --filter "FullyQualifiedName~Analytics"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add analytics"
```

---

## Task 7: Scheduled publisher background job

**Files:**
- Create: `src/Blog.Api/Services/ScheduledPublisherHostedService.cs`

**Interfaces:**
- Consumes: `IServiceScopeFactory`, `BlogDbContext`.
- Produces: hosted service registered in `Program.cs`.

- [ ] **Step 1: Write failing test**

Add an integration test that seeds two scheduled posts (one due, one future) and invokes the hosted service directly.

```csharp
[Fact]
public async Task ScheduledPublisher_PublishesOnlyDuePosts()
{
    // seed due and future scheduled posts
    // run service once
    // due -> Published, future -> Scheduled
}
```

- [ ] **Step 2: Implement the hosted service**

`ScheduledPublisherHostedService` inherits `BackgroundService`, loops with a 60-second delay, and calls an internal `PublishDuePostsAsync(IServiceScope scope)` method that resolves `BlogDbContext` and updates `Status = Published` and `PublishedAt = UtcNow` for due posts where `Status == Scheduled`.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj --filter "FullyQualifiedName~Scheduled"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add scheduled publisher background job"
```

---

## Task 8: Editor feedback

**Files:**
- Create: `src/Blog.Api/Services/IFeedbackService.cs`
- Create: `src/Blog.Api/Services/FeedbackService.cs`
- Create: `src/Blog.Api/Controllers/FeedbackController.cs`
- Create: `tests/Blog.Api.Tests/FeedbackControllerTests.cs`

**Interfaces:**
- Consumes: `BlogDbContext`, `Feedback`, `CreateFeedbackRequest`, `FeedbackDto`.
- Produces:
  - `IFeedbackService.CreateAsync(Guid postId, string authorId, string authorName, string quotedText, string comment)`.
  - `IFeedbackService.ListForPostAsync(Guid postId)`.
  - `IFeedbackService.DeleteAsync(Guid feedbackId, string userId, bool isAdmin)`.
  - Controller routes under `/api/posts/{postId}/feedback`.

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task EditorCanAddFeedback_WithQuotedText()
{
    // editor client POSTs feedback on a draft
    // GET list includes QuotedText and Comment
}
```

- [ ] **Step 2: Implement service and controller**

Admin/Editor can list. Editor can create. Admin or the feedback author can delete.

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj --filter "FullyQualifiedName~Feedback"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add editor feedback endpoints"
```

---

## Task 9: API authentication and authorization

**Files:**
- Modify: `src/Blog.Api/Program.cs`
- Modify: `tests/Blog.Api.Tests/TestAuthHandler.cs` (already created)

**Interfaces:**
- Consumes: Authentik authority/audience/client-id settings from configuration.
- Produces: API that rejects anonymous requests on protected endpoints and enforces roles.

- [ ] **Step 1: Configure JWT bearer auth**

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Authentik:Authority"]!;
        options.Audience = builder.Configuration["Authentik:Audience"]!;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();
```

- [ ] **Step 2: Map role claim to ASP.NET Core roles**

If Authentik emits `role` as a string array, ensure `ClaimsIdentity` maps each value to a `ClaimTypes.Role` claim in a custom `JwtBearerEvents.TokenValidated` handler.

- [ ] **Step 3: Write a test for an unknown role**

```csharp
[Fact]
public async Task AdminEndpoint_WithNoRoleClaim_Returns403()
{
    var client = _factory.CreateClientWithRole("");
    var response = await client.PostAsJsonAsync("/api/posts", new CreatePostRequest("x", "y"));
    response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Blog.Api.Tests/Blog.Api.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Blog.Api tests/Blog.Api.Tests
git commit -m "feat(api): add JWT auth and role authorization"
```

---

## Task 10: Blazor Web project scaffolding

**Files:**
- Create: `src/Blog.Web/Blog.Web.csproj`
- Create: `src/Blog.Web/Program.cs`
- Create: `src/Blog.Web/App.razor`
- Create: `src/Blog.Web/_Imports.razor`
- Create: `src/Blog.Web/Components/Layout/MainLayout.razor`
- Create: `src/Blog.Web/Components/Layout/NavMenu.razor`
- Create: `src/Blog.Web/Components/Routes.razor`

**Interfaces:**
- Consumes: `Blog.Shared`.
- Produces: runnable Blazor server app with MudBlazor.

- [ ] **Step 1: Create project and add MudBlazor**

Reference `MudBlazor` and `Blog.Shared`.

- [ ] **Step 2: Configure OIDC and token forwarding**

In `Program.cs`:

```csharp
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "Cookies";
    options.DefaultChallengeScheme = "OpenIdConnect";
})
.AddCookie("Cookies")
.AddOpenIdConnect("OpenIdConnect", options =>
{
    options.Authority = builder.Configuration["Authentik:Authority"]!;
    options.ClientId = builder.Configuration["Authentik:ClientId"]!;
    options.ClientSecret = builder.Configuration["Authentik:ClientSecret"]!;
    options.ResponseType = "code";
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "name";
    options.TokenValidationParameters.RoleClaimType = "role";
    options.SaveTokens = true;
});

builder.Services.AddHttpClient("BlogApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"]!);
}).AddUserAccessTokenHandler();
```

- [ ] **Step 3: Wire MudBlazor**

Add `MudThemeProvider`, `MudDialogProvider`, `MudSnackbarProvider` in `MainLayout.razor`.

- [ ] **Step 4: Verify the app runs**

Run: `dotnet run --project src/Blog.Web`
Expected: app starts without errors (will 500 on API calls until Task 12).

- [ ] **Step 5: Commit**

```bash
git add src/Blog.Web
git commit -m "feat(web): scaffold Blazor server app with MudBlazor and OIDC"
```

---

## Task 11: Public Blazor pages

**Files:**
- Create: `src/Blog.Web/Services/IBlogApiClient.cs`
- Create: `src/Blog.Web/Services/BlogApiClient.cs`
- Create: `src/Blog.Web/Components/Pages/Home.razor`
- Create: `src/Blog.Web/Components/Pages/PostDetail.razor`
- Create: `src/Blog.Web/Components/Pages/TagPage.razor`
- Create: `src/Blog.Web/Components/Pages/SearchPage.razor`
- Create: `tests/Blog.Web.Tests/Blog.Web.Tests.csproj`
- Create: `tests/Blog.Web.Tests/BlogApiClientTests.cs`

**Interfaces:**
- Consumes: `IHttpClientFactory`, `PostDto`, `PagedResult<T>`, `PostListRequest`.
- Produces: `IBlogApiClient` with methods `GetPostsAsync`, `GetPostAsync(string slug)`, `SearchAsync`, `RecordPageViewAsync`.

- [ ] **Step 1: Write a failing test for `BlogApiClient`**

Use `HttpMessageHandler` mock to verify `GetPostsAsync` sends `GET /api/posts` and deserializes `PagedResult<PostDto>`.

- [ ] **Step 2: Implement `BlogApiClient`**

Methods call API endpoints with `System.Net.Http.Json`.

- [ ] **Step 3: Implement public pages**

`Home` lists posts with pagination. `PostDetail` fetches by slug and calls analytics endpoint. `TagPage`/`SearchPage` use `PostListRequest`.

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Blog.Web.Tests/Blog.Web.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Blog.Web tests/Blog.Web.Tests
git commit -m "feat(web): add public blog pages and API client"
```

---

## Task 12: Admin Blazor pages

**Files:**
- Create: `src/Blog.Web/Components/Pages/Admin/PostEditor.razor`
- Create: `src/Blog.Web/Components/Pages/Admin/MediaLibrary.razor`
- Create: `src/Blog.Web/Components/Pages/Admin/TagsAndCategories.razor`
- Create: `src/Blog.Web/Components/Pages/Admin/FeedbackReview.razor`
- Create: `src/Blog.Web/Components/Pages/Admin/Analytics.razor`
- Create: `src/Blog.Web/Services/IMarkdownRenderer.cs`
- Create: `src/Blog.Web/Services/MarkdownRenderer.cs`

**Interfaces:**
- Consumes: `IBlogApiClient`, `IHttpClientFactory` (for multipart upload), Markdig.
- Produces: admin UI accessible only to `Admin` role.

- [ ] **Step 1: Add Markdig and a Markdown renderer service**

`IMarkdownRenderer.ToHtml(string markdown)` returns sanitized HTML (for v1, raw Markdig pipeline; consider sanitization library if user input reaches public).

- [ ] **Step 2: Extend `IBlogApiClient` for admin operations**

Add `CreatePostAsync`, `UpdatePostAsync`, `PublishAsync`, `UploadMediaAsync`, `CreateTagAsync`, `CreateCategoryAsync`, `GetFeedbackAsync`, `GetAnalyticsAsync`.

- [ ] **Step 3: Implement PostEditor**

Two-pane layout: Markdown textarea on the left, live preview on the right. Banner image upload (scoped to post). Inline image upload with scope toggle. Save as draft or schedule/publish.

- [ ] **Step 4: Implement remaining admin pages**

Media library, tags/categories, feedback review, analytics dashboard. Use MudBlazor components.

- [ ] **Step 5: Manual smoke test**

Run both API and Web locally. Log in via a test Authentik user with role `Admin`. Create a draft, upload a banner, save, and verify the API persisted it.

- [ ] **Step 6: Commit**

```bash
git add src/Blog.Web
git commit -m "feat(web): add admin pages"
```

---

## Task 13: Editor Blazor pages

**Files:**
- Create: `src/Blog.Web/Components/Pages/Editor/Drafts.razor`
- Create: `src/Blog.Web/Components/Pages/Editor/DraftReader.razor`

**Interfaces:**
- Consumes: `IBlogApiClient`, `FeedbackDto`, `CreateFeedbackRequest`.
- Produces: editor UI accessible to `Editor` or `Admin` role.

- [ ] **Step 1: Implement draft list**

List all posts with `Status != Published`. Link to reader.

- [ ] **Step 2: Implement draft reader with feedback**

Render post content. Use JavaScript interop to capture text selection and invoke a C# method with the selected text. Open a MudDialog to enter the comment, then POST `/api/posts/{postId}/feedback`.

- [ ] **Step 3: Smoke test**

As Editor, open a draft, select text, add feedback, verify it appears in Admin feedback review.

- [ ] **Step 4: Commit**

```bash
git add src/Blog.Web
git commit -m "feat(web): add editor draft reader and feedback"
```

---

## Task 14: Local development environment

**Files:**
- Create: `compose.yaml`
- Create: `.env.example`
- Modify: `README.md` (create if missing)

**Interfaces:**
- Produces: local Postgres reachable by the API.

- [ ] **Step 1: Add Docker Compose**

```yaml
services:
  postgres:
    image: postgres:18-alpine
    environment:
      POSTGRES_USER: blog
      POSTGRES_PASSWORD: blog
      POSTGRES_DB: blog
    ports:
      - "5432:5432"
    volumes:
      - blog-postgres-data:/var/lib/postgresql/data
volumes:
  blog-postgres-data:
```

- [ ] **Step 2: Add `.env.example`**

```
ConnectionStrings__BlogDb=Host=localhost;Database=blog;Username=blog;Password=blog
Authentik__Authority=https://authentik.example.com/application/o/blog/
Authentik__ClientId=...
Authentik__ClientSecret=...
Authentik__Audience=...
ApiBaseUrl=http://localhost:5000/
FileStorage__BasePath=./uploads
```

- [ ] **Step 3: Document dev startup**

Add instructions for `docker compose up -d`, `dotnet ef database update`, `dotnet run --project src/Blog.Api`, `dotnet run --project src/Blog.Web`.

- [ ] **Step 4: Verify compose**

Run: `docker compose up -d` then `dotnet ef database update -p src/Blog.Api -s src/Blog.Api`
Expected: database is created and migrations apply.

- [ ] **Step 5: Commit**

```bash
git add compose.yaml .env.example README.md
git commit -m "chore: add local docker compose and dev docs"
```

---

## Task 15: Kubernetes deployment manifests

**Files:**
- Create: `k8s/namespace.yaml`
- Create: `k8s/configmap.yaml`
- Create: `k8s/secret.yaml`
- Create: `k8s/pvc.yaml`
- Create: `k8s/cnpg-cluster.yaml`
- Create: `k8s/api-deployment.yaml`
- Create: `k8s/api-service.yaml`
- Create: `k8s/web-deployment.yaml`
- Create: `k8s/web-service.yaml`
- Create: `k8s/ingress.yaml`

**Interfaces:**
- Produces: deployable K8s resources.

- [ ] **Step 1: Create namespace, config map, secret templates**

ConfigMap: `ASPNETCORE_ENVIRONMENT=Production`, `ApiBaseUrl=https://api.blog.example.com/`, Authentik authority/audience (non-secret). Secret: connection string (`Host=blog-db-rw;Database=blog;Username=app;Password=...`), OIDC client secret.

- [ ] **Step 2: Create media PVC and API deployment**

Create `k8s/pvc.yaml` with `storageClassName: longhorn`. Mount the PVC at `/app/uploads` in the API deployment. Container image placeholder `ghcr.io/yourname/blog-api:latest`. Set readiness/liveness probes on `/health` (add a minimal health endpoint if missing).

- [ ] **Step 3: Add CNPG cluster manifest**

Create `k8s/cnpg-cluster.yaml` with a `Cluster` resource named `blog-db`, PostgreSQL 18 image, `storage.storageClass=longhorn`, and an `app` user/database. The API connects via `blog-db-rw.blog.svc.cluster.local`.

- [ ] **Step 4: Create Web deployment**

Container image `ghcr.io/yourname/blog-web:latest`. Reference API via ConfigMap.

- [ ] **Step 5: Create services and ingress**

Ingress hosts: `blog.example.com` → `blog-web-service:80`, `api.blog.example.com` → `blog-api-service:80`. Use cert-manager annotations if desired.

- [ ] **Step 6: Add a health check endpoint**

In `Blog.Api`, add `MapHealthChecks("/health")`.

- [ ] **Step 7: Validate manifests**

Run: `kubectl apply --dry-run=client -f k8s/`
Expected: all resources validate.

- [ ] **Step 8: Commit**

```bash
git add k8s
git commit -m "chore: add kubernetes manifests"
```

---

## Task 16: End-to-end smoke test

**Files:**
- Modify: `README.md`
- Create: `docs/runbooks/smoke-test.md` (optional)

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test`
Expected: all tests pass.

- [ ] **Step 2: Manual local smoke test**

1. `docker compose up -d`
2. `dotnet run --project src/Blog.Api`
3. `dotnet run --project src/Blog.Web`
4. Log in as Admin, create a scheduled post, verify scheduled publisher publishes it.
5. Log in as Editor, leave feedback on a draft.
6. Upload media, set banner, publish a post.
7. View public post and confirm analytics increments.

- [ ] **Step 3: Commit**

```bash
git add README.md docs/runbooks
git commit -m "docs: add smoke-test runbook"
```

---

## Self-review checklist

- [ ] **Spec coverage:** Every spec section (entities, auth, API, UI, jobs, dev, K8s) maps to at least one task.
- [ ] **Step scan:** Each step is a single action with a checkable result; no placeholders or vague instructions.
- [ ] **Type consistency:** DTOs, entity names, and service method signatures match across tasks.
- [ ] **Review Focus:** Each of the five review-focus items has a test in its owning task.
- [ ] **Proportion:** Plan is detailed but does not contain full method bodies for routine code.
