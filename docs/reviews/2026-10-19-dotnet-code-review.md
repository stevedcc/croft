# .NET code review: `src/` on Croft 0.6.2

Date: 2026-10-19. Commit reviewed: `0e9e697` ("Croft 0.6.2"). Scope: the C# and Razor under
`src/Croft.Api`, `src/Croft.Web` and `src/Croft.Shared`, read for correctness and idiomatic
.NET 10 / C# 14 along the axes the brief named: async correctness, EF Core usage, DI lifetimes,
disposal, exception handling and logging, nullable annotations, allocations on hot paths, dead
code and duplication. Style nits a formatter would fix are left out. Nothing outside this file
was changed.

**How claims were checked.** Every finding quotes the code it is about. Where a claim rests on
running something, the run is named. Where it could not be run here, the finding says
"unverified".

| Check | Result |
|---|---|
| `dotnet build Croft.slnx` (SDK 10.0.402, as `global.json` requires) | 0 warnings, 0 errors |
| `dotnet test tests/Croft.Web.Tests --no-build` | Passed 1057, Failed 31, Total 1088 |
| `dotnet test tests/Croft.Api.Tests` | Not run: no Docker/Podman socket in this container |

The 31 Web unit-test failures are all Testcontainers start-up failures (`Docker.DotNet`
endpoint not reachable), the same environmental limitation the public-release audit recorded as
its finding I-5 ([`2026-10-19-public-release-readiness.md`](2026-10-19-public-release-readiness.md),
draft PR #3). No test failed on an assertion. Nothing below depends on those tests.

Findings that the public-release audit already carries (hard-coded fediverse handle in `src/`,
the OIDC scope list, the Web test suite's container dependency, and so on) are not repeated;
they are referenced by their audit numbers where they touch a finding here.

Severity: **Critical** = data loss, security hole or an outage path on normal input;
**Important** = wrong behaviour, a resource leak or a measurable cost on a hot path that a real
user or crawler will hit; **Minor** = correct today but fragile, wasteful or against the
project's own conventions.

No Critical findings.

---

## Important

### I-1. Post-commit cache invalidation and `pg_notify` run under the request's cancellation token

`src/Croft.Api/Services/PostService.cs:377-379` (the same shape at `:423-425`, `:439-441`,
`:488-490`, `:517-519`):

```csharp
db.Posts.Add(post);
await db.SaveChangesAsync(ct);
await cacheNotifier.ContentChangedAsync(db, CacheGroup.All, ct);
```

`src/Croft.Api/Services/CacheNotifier.cs:24-38`:

```csharp
public async Task ContentChangedAsync(BlogDbContext db, CacheGroup changed, CancellationToken ct = default)
{
    foreach (var group in Groups)
    {
        ...
        await cache.InvalidateAsync(group, ct);
        var payload = group.ToString().ToLowerInvariant();
        await db.Database.ExecuteSqlAsync($"SELECT pg_notify({Channel}, {payload})", ct);
    }
}
```

`src/Croft.Api/Services/PublicReadCache.cs:113-116` honours the token before it bumps the
generation:

```csharp
public async Task InvalidateAsync(CacheGroup group, CancellationToken ct = default)
{
    var slot = Slot(group);
    await _gate.WaitAsync(ct);
```

**What is wrong.** Once `SaveChangesAsync` has committed, the write is durable, but the
invalidation of the local cache and the `NOTIFY` to the other replicas are still cancellable
by the same token. `ct` is the request's `RequestAborted`.

**Scenario.** An editor hits Publish and closes the tab (or the browser retries, or the
reverse proxy times out the request) while the API is between the commit and the notify. The
post is published in the database. The replica that handled the write keeps serving the old
`posts:slug` and `posts:list` entries, and the other replicas never hear about the change.
Readers see stale content for up to the five-minute safety expiry (`Program.cs:92`). The
`CacheGroup.All` loop makes this worse: cancellation after the first group leaves categories
invalidated but posts and pages not.

**Fix.** Treat everything after the commit as a post-commit side effect: pass
`CancellationToken.None` to `ContentChangedAsync` from the service methods (or have
`ContentChangedAsync` ignore its parameter after the first awaited call), and run the four
`pg_notify` calls as one statement (`SELECT pg_notify(...), pg_notify(...)`) so the
invalidation is atomic from the listener's point of view. Keep the request token for the
reads and the `SaveChangesAsync` itself.

### I-2. `TagsPage` lets any non-401 API failure terminate the circuit

`src/Croft.Web/Components/Pages/Admin/TagsPage.razor:177-200`:

```csharp
protected override async Task OnInitializedAsync()
{
    await LoadCategoriesAsync();
    await LoadTagsAsync();
}

private async Task LoadCategoriesAsync()
{
    try
    {
        var result = await Api.GetCategoriesAsync(_categoryPage, PageSize, _categoryFilter);
        ...
    }
    catch (SessionExpiredException ex)
    {
        _error = ex.Message;
    }
}
```

The same pattern is in `LoadTagsAsync` (`:203-219`), `AddCategoryAsync` (`:290-308`) and
`AddTagAsync` (`:310-328`). The client throws plain `HttpRequestException` for every other
failure, `src/Croft.Web/Services/BlogApiClient.cs:311-312`:

```csharp
var response = await SendAsync(http => http.PostAsJsonAsync("/api/categories", name, ct), ct);
response.EnsureSuccessStatusCode();
```

`SessionExpiredException` derives from `HttpRequestException`
(`src/Croft.Web/Services/SessionExpiredException.cs:10`), so the catch is strictly narrower
than the sibling pages, which catch `HttpRequestException`
(`PostEditPage.razor:197-200`, `PostAnalyticsPage.razor:42-46`, `MediaGrid.razor:149-155`).

**Scenario.** The API is restarting (a rolling deploy) when an admin opens Tags &
categories: `OnInitializedAsync` throws during prerender, so the page answers 500, and if the
circuit was already up it is torn down with "An unhandled error has occurred". Or, with I-3
below: an admin types a 129-character category name and presses Add. The API answers 500,
`EnsureSuccessStatusCode` throws `HttpRequestException`, nothing catches it, and the circuit
dies with the admin's work on the page.

**Fix.** Catch `HttpRequestException` in the four methods (the `SessionExpiredException` case
is covered by it) and show `ex.Message`, as the other admin pages do.

### I-3. `CategoryService.CreateAsync` and `TagService.CreateAsync` skip validation, so long names become 500s

`src/Croft.Api/Services/CategoryService.cs:102-114`:

```csharp
public async Task<CategoryDto> CreateAsync(string name, CancellationToken ct = default)
{
    var slug = slugService.GenerateSlug(name);
    var existing = await db.Categories.FirstOrDefaultAsync(c => c.Slug == slug, ct);
    if (existing is not null)
        return await MapToDtoAsync(existing, ct);

    var category = new Category { Name = name, Slug = slug };
    db.Categories.Add(category);
    await db.SaveChangesAsync(ct);
```

`UpdateAsync` at `:118` starts with `ValidateRequest(request.Name, request.Icon);`;
`CreateAsync` never calls it. `TagService.cs:71-83` and `:87` have the identical gap. The
column is `HasMaxLength(128)` (`Data/Configurations/CategoryConfiguration.cs:13`,
`TagConfiguration.cs:13`) and the controllers bind a bare string
(`CategoriesController.cs:39` and `TagsController.cs:35`, `[FromBody] string name`).

**Scenario.** `POST /api/categories` with a 129-character name (or an empty string, or
whitespace: the slug generator decides what happens then). Npgsql rejects the insert with
`22001 string_data_right_truncation`; `ApiExceptionHandler.cs:14-21` maps only
`UniqueViolation` to 409, so this is a 500, logged at Error with a stack trace, and the admin
UI crashes per I-2.

**Fix.** Call `ValidateRequest(name, icon: null)` / `ValidateRequest(name)` at the top of both
`CreateAsync` methods. The existing `ValidationException` → 400 mapping then does the rest.

### I-4. The CSP header is rebuilt from scratch several times on every response, including static assets

`src/Croft.Web/Security/Csp/CspMiddleware.cs:29-89`:

```csharp
public async Task InvokeAsync(HttpContext context)
{
    var policy = new CspPolicy().Merge(_template);
    var reportOnlyPolicy = new CspPolicy();
    context.Features.Set<ICspFeature>(new CspFeature(policy, reportOnlyPolicy));
    foreach (var contributor in contributors)
    {
        contributor.Contribute(context, policy);
    }
    ...
    policy.Changed += WritePolicy;
    reportOnlyPolicy.Changed += WritePolicy;
    WritePolicy();

    context.Response.OnStarting(() =>
    {
        var kind = CspDocument.KindOf(context);
        foreach (var contributor in documentContributors)
        {
            contributor.Contribute(context, policy, reportOnlyPolicy, kind);
        }
        return Task.CompletedTask;
    });
```

`CspPolicy.ToString()` (`CspPolicy.cs:84-91`) runs LINQ over every directive and joins
strings; it is called from `WritePolicy` on the initial call **and on every `Changed`
event**. The document contributors add directives one at a time:
`TrustedTypesCspContributor.cs:24-28` is four `Add` calls, `ScriptSourcesCspContributor.cs:43-58`
one more, and it concatenates `origin + path` per request even though the origin is
configured. So one static reading page costs: a deep copy of the template (a dictionary and
about seventeen lists), one header build up front, then five or six more header builds during
`OnStarting`, each one re-sorting and re-joining the whole policy. Interactive documents add
the MudBlazor theme and admin-area contributors on top. The same copy-and-build happens for
every `MapStaticAssets` response, `/health/*`, `/api/media/{id}` and the analytics beacon,
none of which needs a document CSP (the middleware is registered before routing,
`Program.cs:420`).

**Scenario.** A reader loads a post: one document plus roughly twenty assets (fonts, CSS,
JS, images). That is about twenty policy copies and thirty-odd `ToString` runs before the
page is on screen, on the one process whose job is to render static pages cheaply. The brief
called this out as a hot path and it is.

**Fix.** Precompute the final header strings once at startup for the small set of outcomes
(static document, interactive document, interactive admin document, sign-out interstitial,
non-document) and set the header directly in `OnStarting`; keep the mutable `CspPolicy` only
for the rare caller that actually uses `ICspFeature` to add a source, by building it lazily
on first access. Suppress the whole thing for responses that are not HTML documents.

### I-5. Markdown is parsed and rendered on every request, and on every keystroke in the editor

Reading pages: `src/Croft.Web/Components/Pages/Post.razor:94-96` and
`StaticPage.razor:67-68`:

```csharp
_post = post;
_rendered = Markdown.Render(post.MarkdownContent);
```

`MarkdownRenderer.Render` (`Services/MarkdownRenderer.cs:23-46`) parses with Markdig, walks
the tree for headings and renders to HTML each time. The Web app has no output or response
caching for the reading pages (nothing in `Program.cs` registers `AddOutputCache` or
`UseResponseCaching`), and the API returns the raw Markdown, so the only cache in the chain is
the API's `IMemoryCache` of the DTO. A 200 000-character post (`PostContent.MaxLength`) is
re-rendered for every anonymous GET.

Editor: `src/Croft.Web/Components/Pages/Admin/PostEditPage.razor:558`:

```csharp
private RenderedMarkdown Rendered => Markdown.Render(_model.MarkdownContent, new MarkdownRenderOptions { SourceLines = true });
```

`Rendered` is a property read from the markup (`:67` and `:73`), and the form reports every
edit (`PostEditorForm.razor:31-41`, `@bind-Value:after="ModelChangedAsync"`), so each
keystroke re-renders the component and re-parses the whole document, with source-line
attributes, on the circuit. `PreviewPost` (`:560-577`) likewise allocates a new `PostDto`
and tag list per render, and `PostEditorForm.razor:9` serialises
`DraftBackup.ExtraJson(Model)` to JSON per render.

**Scenario.** A reader-facing one: a crawler walks 2 000 posts; each GET does a full
Markdig pass whose output has not changed since the post was last edited. An editor-facing
one: typing in a 60K post, each keystroke costs a full parse plus HTML render on the server,
and the measured W-009 lost-character problem in the same component is the kind of thing that
gets worse as that per-keystroke cost grows.

**Fix.** Reading pages: cache `RenderedMarkdown` keyed on `(post.Id, post.ContentEditedAt ??
post.UpdatedAt)` in a bounded `IMemoryCache` (size-limited like the API's), or add
`AddOutputCache` with a policy for the seven static routes tagged by post and evicted by the
same LISTEN/NOTIFY the API already has. Editor: memoise `Rendered` on the content string
(`if (!ReferenceEquals(content, _lastContent)) { ... }`) and debounce the preview.

### I-6. Public read cache keys use unclamped, user-controlled values, so one crawler can evict the hot set

`src/Croft.Api/Services/PostService.cs:275-279`:

```csharp
if (CacheablePublicVariant(publicOnly, includeAdminCounts))
    return await cache.GetOrCreateAsync(CacheGroup.Posts,
        CacheKey("posts:list", request.SearchTerm, request.TagSlug, request.CategorySlug,
            request.Status, request.Kind, request.Page, request.PageSize),
```

`:177` keys `pages:list` on raw `page, pageSize`; `:227` keys `posts:related` on raw `count`
while the loader clamps it (`:250`, `var take = Math.Clamp(count, 1, 10);`) and
`PagingHelper.cs:17` clamps `pageSize` to 1..100 only *inside* the loader. The cache is
`SizeLimit = 2000` entries (`Program.cs:92`) with LRU-style compaction.

**Scenario.** Anonymous `GET /api/posts?searchTerm=<random>&pageSize=<random>` from a
scraper, or simply a bot following `?page=N` for N up to 10 000 on an empty category: every
distinct value is a new key holding an identical (often empty) result, and each new entry
evicts a real one. `?pageSize=100`, `?pageSize=101` and `?pageSize=99999` are three keys for
one result. Free-text search terms give an unbounded key space. The cache stops helping
exactly when the site is being crawled.

**Fix.** Normalise before keying: clamp `page`, `pageSize` and `count` with the same helper
the loader uses, trim and lower-case the search term and skip the cache altogether when
`SearchTerm` is set (search results are not worth caching per term), and cap `page` for
anonymous reads at `ceil(TotalCount / pageSize)` or a fixed maximum.

### I-7. The Web media proxy never disposes its `HttpResponseMessage` or `HttpRequestMessage`

`src/Croft.Web/Program.cs:454-486`:

```csharp
var client = httpClientFactory.CreateClient("BlogApi");
var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media/{id}");
...
var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

context.Response.StatusCode = (int)response.StatusCode;
if (!response.IsSuccessStatusCode)
    return;
...
await response.Content.CopyToAsync(context.Response.Body, ct);
```

`ResponseHeadersRead` means the connection stays checked out until the content stream is
disposed. On the non-success path the method returns with the body unread and the response
undisposed; on the success path the stream is only released when the GC finalises it. The
analytics proxy (`:545`, `await client.SendAsync(forward, ct);`) discards its response the
same way.

**Scenario.** A page with twenty draft images viewed by an anonymous reader (every image a
403), or a burst of 404s from a stale sitemap: each one holds a pooled connection to the API
until finalisation. Under load the `HttpClient` handler's connection pool fills and
unrelated requests to the API start queueing. This is the classic `ResponseHeadersRead`
leak.

**Fix.** `using var request = ...; using var response = await client.SendAsync(...)` in both
endpoints. Also give the `"BlogApi"` client a `Timeout` shorter than the default 100 s
(`Program.cs:193-196` sets only `BaseAddress`), since a page render holds a rate-limiter
permit while it waits.

### I-8. Admin pages load their data twice per full page load (no `[PersistentState]`)

`AGENTS.md` ("Interactivity"): "Data a page loads there must survive the handoff with
`[PersistentState]` and the `??=` pattern, or it is fetched from the API twice." Only
`MainLayout` does this (`Layout/MainLayout.razor:55`). The admin and editor pages do not:

- `Pages/Admin/PostEditPage.razor:162-201`: `GetTagsAsync`, `GetPostByIdAsync`,
  `GetFeedbackAsync` in `OnInitializedAsync`, three calls, run twice.
- `Pages/Admin/TagsPage.razor:177-181`: two calls, run twice.
- `Pages/Admin/PostAnalyticsPage.razor:31-49`: one call, run twice.
- `Admin/MediaGrid.razor:128`: `OnInitializedAsync() => LoadAsync()`, run twice.
- `Pages/Editor/DraftReviewPage.razor:84-125`: three calls in `OnParametersSetAsync`,
  run twice.

**Scenario.** An admin opens the editor on a 200K post: the API serves the post, its tags
and its feedback during prerender, the circuit connects, and the same three requests go out
again before anything is interactive. Every admin page pays double API load and double token
look-ups (`TokenSessionEvents.cs:76-78` hits the token store per request). On the
`PostgresTokenStore` that is a `SELECT ... FOR UPDATE` per call.

**Fix.** The pattern `MainLayout` already uses: a `[PersistentState]` record of the loaded
DTOs, `??=` in the lifecycle method. `PostEditPage` is the one to do first.

### I-9. `MediaController.Get` loads the whole `Post` row to read one enum, on every image request

`src/Croft.Api/Controllers/MediaController.cs:65-80`:

```csharp
var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
...
if (asset.Scope == MediaAssetScope.PostSpecific)
{
    post = await db.Posts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == asset.PostId, ct);
    if (post is not null && post.Status != Croft.Shared.Enums.PostStatus.Published)
```

`Post` carries `MarkdownContent` (up to 200K characters) and the `tsvector` search column.
The controller also injects `BlogDbContext` directly (`:14`) against the "thin controllers,
logic in `Services/`" convention, and `fileStorage.OpenRead(asset.FilePath)` (`:80`) throws
`FileNotFoundException` when the file is gone (a PVC restore, a manual clean-up), which the
exception handler turns into a 500 with a stack trace rather than a 404.

**Scenario.** A post with ten inline images: ten image GETs, each pulling the post's full
Markdown and search vector across the wire from Postgres to decide "is it published". The
reading page that embeds them is the hottest anonymous path after the post itself.

**Fix.** `await db.Posts.AsNoTracking().Where(p => p.Id == asset.PostId).Select(p => (PostStatus?)p.Status).FirstOrDefaultAsync(ct)`,
moved into `IMediaService` with the authorisation decision; return `NotFound()` when the file
is missing.

---

## Minor

### M-1. `CategoryService` and `TagService` cache regardless of who is asking

`src/Croft.Api/Services/CategoryService.cs:16-17` and `:53-57`:

```csharp
public Task<IReadOnlyList<CategoryDto>> ListAsync(CancellationToken ct = default) =>
    cache.GetOrCreateAsync(CacheGroup.Categories, "categories:list", LoadListAsync, ct);
```

`PostService.cs:67-72` gates every cached read on `CacheablePublicRead()` ("only the
anonymous variant of a read is cached — the same public-read boundary the read-replica
routing uses"). The category and tag services never check. The data is public either way, so
nothing leaks, but an admin who just renamed a category and reloads Tags & categories can be
served the pre-rename list from the cache until the NOTIFY lands, and the documented contract
is not what the code does. **Fix.** Apply the same gate, or document the exception.

### M-2. No per-key stampede protection, and null results are never cached

`src/Croft.Api/Services/PublicReadCache.cs:69-78`: `GetOrCreateAsync` runs the factory
outside any lock; the comment at `:92-93` acknowledges that concurrent refills of one key
are "the common case after an invalidation". `if (value is null) return value;` (`:77-78`)
means a 404 slug is looked up on every request, and `PostService.cs:108-114` then tries the
GUID form too: two queries per miss. A burst of hits on a deleted post's URL (a shared link)
is two uncached queries per hit on the primary (anonymous reads route to the replica only
when `Database:ReadHost` is set, `BlogDbConnectionString.cs:69-72`). **Fix.** A per-key
`Lazy<Task<T>>`/`SemaphoreSlim` map for refills, and a short negative-cache entry for misses.

### M-3. One cache write sends four `InvalidateAsync` and four `pg_notify` round trips

`src/Croft.Api/Services/CacheNotifier.cs:26-37`: the loop over `Groups` does four lock
acquisitions and four `ExecuteSqlAsync` calls for `CacheGroup.All`. One statement with four
`pg_notify` calls, or one payload naming all groups, halves the post-commit latency and makes
the listener see one atomic event (see I-1).

### M-4. Uploads are buffered twice in memory

`src/Croft.Api/Services/MediaService.cs:47-49`:

```csharp
using var uploadStream = file.OpenReadStream();
using var memoryStream = new MemoryStream();
await uploadStream.CopyToAsync(memoryStream, ct);
```

The request body (up to 10 MB, `MediaController.cs:18-19`) is already buffered by the form
feature; copying it into a `MemoryStream` doubles the peak. `new
FileExtensionContentTypeProvider()` (`:39`) is also constructed per upload; it builds a
400-entry dictionary each time. **Fix.** Stream `uploadStream` straight into
`IFileStorage.SaveAsync`, and make the provider a static readonly field.

### M-5. `AnalyticsService` rebuilds its INSERT SQL from the EF model on every page view

`src/Croft.Api/Services/AnalyticsService.cs:54-89`: `InsertPageViewOnceAsync` calls
`db.Model.FindEntityType`, `GetTableName`, `StoreObjectIdentifier.Table` and column-name
look-ups per beacon, then runs the insert, the post `ViewCount` increment and the daily
upsert as three round trips. `HashIp` (`:124`) re-encodes the HMAC key per call.
`PageViewRetentionHostedService.cs:59-65` hard-codes `"PageViews"` / `"Day"` in its
`DELETE`, so one side derives names from the model and the other does not; a rename breaks
retention silently. **Fix.** Compute the names once (static or in the constructor), key the
HMAC bytes once, and either use the model-derived names in both places or plain EF for both.

### M-6. Dead `catch (UnauthorizedAccessException)` in `PostsController`

`src/Croft.Api/Controllers/PostsController.cs:67-70`:

```csharp
catch (UnauthorizedAccessException)
{
    return Forbid();
}
```

`PostService.UpdateAsync` throws `ForbiddenException`, which `ApiExceptionHandler.cs:17`
maps to 403. Nothing in `src/` throws `UnauthorizedAccessException`. Dead code that suggests
a contract the service does not have. **Fix.** Delete the try/catch.

### M-7. `DateTimeNormalizer.NormalizeToUtc` is duplicated inline in `PostService.PublishAsync`

`src/Croft.Api/Services/PostService.cs:469-473`:

```csharp
var scheduledAt = request.PublishedAt.HasValue
    ? (request.PublishedAt.Value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(request.PublishedAt.Value, DateTimeKind.Utc)
        : request.PublishedAt.Value.ToUniversalTime())
    : now;
```

`DateTimeNormalizer` is registered as a singleton (`Program.cs:96`) for exactly this. Two
implementations of the UTC rule will drift. **Fix.** Inject and call the normaliser.

### M-8. N+1 queries in tag resolution and correlated counts in every summary row

`src/Croft.Api/Services/PostService.cs:542-543`:

```csharp
var tag = await db.Tags.FirstOrDefaultAsync(t => t.Slug == slug, ct)
    ?? new Tag { Name = name, Slug = slug };
```

One query per tag name on every create/update. `ToSummaries` (`:573-608`) projects
`p.Category.Posts.Count(c => c.Status == PostStatus.Published)` and
`t.Posts.Count(...)` per tag, so a page of ten summaries with five tags each is sixty
correlated subqueries in one statement. It is one round trip, so not an N+1, but it is the
most expensive public query in the API and runs for every uncached list page. **Fix.** Load
the matching tags in one `Where(t => slugs.Contains(t.Slug))`; consider a materialised
published-count column on `Tag`/`Category` maintained by the existing `ExecuteUpdateAsync`
increments.

### M-9. Quadratic scan in `ContentImporter`

`src/Croft.Api/Services/ContentExport/ContentImporter.cs:63`:
`media.Single(asset => asset.Id == item.Id).PostId = item.PostId;` inside a loop over the
media items. O(n²) over the whole media library on import; a dictionary by id makes it
linear.

### M-10. `FeedbackService` accepts unbounded `QuotedText` and `Comment`

`src/Croft.Api/Services/FeedbackService.cs:12-29` stores `quotedText` and `comment` as
received; `FeedbackConfiguration.cs:14-15` maps them `IsRequired()` with no length. An
editor (any `Editor` role) can store a 100 MB note. `PostContent.MaxLength` exists for the
post body; nothing bounds feedback. **Fix.** Validate against sensible maxima (quote ≤ 2 000,
comment ≤ 10 000, say) and map `HasMaxLength` accordingly.

### M-11. `LocalFileStorage` path check is loose

`src/Croft.Api/Services/LocalFileStorage.cs:36-39`:

```csharp
var fullPath = Path.GetFullPath(combined);
if (!fullPath.StartsWith(Path.GetFullPath(basePath), StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Invalid file path.");
```

Without a trailing separator, base `/data/uploads` accepts `/data/uploads-old/x`.
`OrdinalIgnoreCase` on Linux lets `/DATA/UPLOADS/...` through the check and then fail on the
file system. Paths come from the database, not the user, so this is defence in depth rather
than a hole. **Fix.** Compare against `Path.TrimEndingDirectorySeparator(base) +
Path.DirectorySeparatorChar` with `StringComparison.Ordinal`.

### M-12. `UserExtensions` falls back to `ClaimTypes.NameIdentifier` that `MapInboundClaims = false` guarantees is absent

`src/Croft.Api/Identity/UserExtensions.cs:7-9`:

```csharp
public static string GetUserId(this ClaimsPrincipal user) =>
    user.FindFirstValue("sub")
    ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? "unknown";
```

`AGENTS.md` says the opposite ("`MapInboundClaims` is off, so don't use
`ClaimTypes.NameIdentifier`"). The `"unknown"` fallback is the real concern: an authenticated
principal with no `sub` becomes author `"unknown"` silently, and ownership checks in
`PostService` compare against that string. **Fix.** Drop the second fallback and throw (or
return 401) when `sub` is missing.

### M-13. Client disconnects are logged as unhandled 500s

`src/Croft.Api/Errors/ApiExceptionHandler.cs:14-21` maps four exception types; everything
else, `OperationCanceledException` included, is a 500 logged at Error with a stack trace. A
reader who navigates away mid-response, or the Web proxy's `ct` firing (I-7), produces an
Error log line per abort. **Unverified** whether the .NET 10 exception-handler middleware
already filters `RequestAborted` cancellations before calling `TryHandleAsync`; if it does
not, add `OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested
=> 499` (or simply return `false`) to the switch.

### M-14. Per-attribute `Regex.IsMatch` and a `ToArray` per activity in the telemetry processor

`src/Croft.Shared/Telemetry/TelemetryPrivacy.cs:53-54`:

```csharp
public static bool CarriesQueryString(string value)
    => Regex.IsMatch(value, @"\?\w+=");
```

A static `Regex.IsMatch(string, pattern)` goes through the regex cache on every call; this is
called per string tag per activity. `[GeneratedRegex]` is the C# 14 answer and is free.
`:105` copies `data.TagObjects.ToArray()` per activity to be able to mutate while
enumerating; a small pooled list or a two-pass scan avoids the allocation on the hot
telemetry path.

### M-15. `UserAccessTokenService` never prunes its per-session lock map

`src/Croft.Web/Services/UserAccessTokenService.cs:26` and `:51-53`:

```csharp
private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new();
...
var refreshLock = _refreshLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
var refreshTask = RefreshUnderLockAsync(sessionId, refreshLock);
return await refreshTask.WaitAsync(ct);
```

The service is a singleton; one `SemaphoreSlim` per session id that ever refreshed is kept
for the process lifetime and never disposed. It is a slow leak proportional to logins, not an
outage. Separately, when the caller's `ct` fires, `WaitAsync(ct)` throws and `refreshTask`
runs on unobserved; a refresh failure then surfaces only as `TaskScheduler.UnobservedTaskException`.
**Fix.** Remove the lock entry when `/logout` or the purge job removes the session, and
attach a continuation that logs a faulted refresh.

### M-16. `BlogApiClient` mutates `DefaultRequestHeaders` and never disposes responses

`src/Croft.Web/Services/BlogApiClient.cs:40-43`:

```csharp
if (!string.IsNullOrEmpty(token))
{
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}
```

The factory hands out a new `HttpClient` per call so this is safe today, but it is the
pattern that becomes a cross-user token leak the day someone caches the client. Per-request
headers belong on an `HttpRequestMessage`. None of the `GetAsync`/`PostAsJsonAsync` results
are disposed; for small JSON bodies that are fully read the cost is low, but it is still the
same `ResponseHeadersRead` risk as I-7 if the content is never read (every
`EnsureSuccessStatusCode` throw leaves the error body unread). `:92-93` interpolates
`tagSlug`/`categorySlug` into the query unescaped, unlike `searchTerm` on `:91`; slugs are
`[a-z0-9-]` by construction, so this is a consistency nit.

### M-17. `UseForwardedHeaders()` after the middleware that reads the client address

`src/Croft.Web/Program.cs:419-448`: `SecurityHeadersMiddleware`, `CspMiddleware`,
`UseRateLimiter` (which partitions by client) and `CircuitAdmissionMiddleware` all run before
`app.UseForwardedHeaders()` at `:447`. With `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (the
k8s deployment per W-013) the framework inserts the forwarded-headers middleware at the very
start as well, so the explicit call runs twice; without it, the rate limiter keys on the
ingress address. **Unverified** which of the two the deployment relies on. **Fix.** One
call, first in the pipeline, with `KnownNetworks`/`KnownProxies` set.

### M-18. Singleton feed and sitemap services fan out N API calls per request, uncached

`src/Croft.Web/Services/SitemapService.cs:72-95` pages through every post
(`GetAllAsync`), `FeedService.cs:29-56` fetches the twenty newest; both build the document
on every GET with no cache or `ETag`. Crawlers fetch `/sitemap.xml` and `/feed.xml` often.
The API's cache hides the database cost but not the HTTP round trips or the XML build. A
five-minute `IMemoryCache` entry, or an `ETag` from the newest `UpdatedAt`, fixes both.

### M-19. `BackchannelLogoutTokenValidator` validates the issuer against the configured string, not the discovered one

`src/Croft.Web/Authentication/BackchannelLogoutTokenValidator.cs:42-43`:

```csharp
ValidateIssuer = true,
ValidIssuer = authority,
```

`configuration` (the discovery document) is fetched on the line above and carries
`Issuer`. The two differ whenever `Oidc:Authority` has a trailing slash or a different host
casing than the IdP announces, and then every logout token is rejected with a validation
error that reads like an attack. **Fix.** `ValidIssuer = configuration.Issuer`.

### M-20. JS module references are never disposed

`src/Croft.Web/Components/Blog/PostBody.razor:22-29`, `Layout/MainLayout.razor:63,117` and
`Layout/ThemeSwitch.razor:18,57-58` each hold an `IJSObjectReference` from `import` and none
implements `IAsyncDisposable`. `DraftReviewPage.razor:220-245` shows the project knows the
pattern (including the `JSDisconnectedException` guard). On the interactive documents these
are circuit-lifetime objects, so the leak is per circuit, not per request; still, the
module handles accumulate on the JS side across enhanced navigations until the circuit ends.

### M-21. `MainLayout` swallows API failures without logging

`src/Croft.Web/Components/Layout/MainLayout.razor:100-104`:

```csharp
catch (HttpRequestException)
{
    // Includes SessionExpiredException: the shell renders without sidebar data (and the
    // circuit survives) while a sign-in redirect or a failing API runs its course.
}
```

Rendering without the sidebar is the right outcome; doing it silently is not. An API that is
down shows as an empty sidebar on every page with nothing in the Web logs. **Fix.** A
`[LoggerMessage]` at Warning with the status code, excluding `SessionExpiredException`.

### M-22. `new Uri(Navigation.Uri)` parsed repeatedly per render

`Pages/Home.razor:65,75,84`, `Category.razor:64,73`, `Tag.razor:63,72`,
`Pages/Admin/PostEditPage.razor:139,187-189,205,260`: the current URI is parsed three or
four times per render for `ShowMasthead`, `CanonicalPath` and the load key, after
`[SupplyParameterFromQuery] PageNumber` has already delivered the value. On static SSR this
is one render so the cost is small; it is duplication that `PageQuery.ReadPageNumber(PageNumber)`
would remove.

### M-23. `SecurityTxtChecker` allocates a list to prune a dictionary

`src/Croft.Web/SiteFiles/SecurityTxtChecker.cs:115`:
`_lastEmitted.Keys.Where(k => !_active.Contains(k)).ToList().ForEach(k => _lastEmitted.Remove(k));`
Daily, so harmless; `_lastEmitted.RemoveWhere`-style `foreach` over a snapshot is the
idiomatic form. Noted only because it sits beside a `#pragma warning disable CA1848, CA2254`
whose reason ("the template varies") is exactly what five `[LoggerMessage]` methods solve.

---

## Checked and found sound

- **Startup and hosted services.** `StartupCommand.Parse` (list patterns), the scheduled
  publisher's wake-up `TaskCompletionSource` and W-030 tick handling, `BackgroundJobLock`
  (transaction-scoped vs session advisory locks, lock timeout lifting), `MigrationRetry`,
  `SecurityTxtHostedService` (`Task.Delay(TickInterval, clock, stoppingToken)` on
  `TimeProvider`, exceptions other than cancellation caught and logged).
- **Public read cache.** `MemoryPublicReadCache` generation check under the gate and the
  W-079 `EvictionReason.Replaced` handling are correct; `IDisposable` disposes the gate.
  LISTEN/NOTIFY listener reconnect loop is bounded and logged.
- **Analytics ingest.** `AnalyticsIngestKey.Matches` uses `CryptographicOperations.FixedTimeEquals`;
  the IP hash is date-salted HMAC; `ExecuteUpdateAsync` increments are atomic;
  `Guid.CreateVersion7` for ids.
- **Logging.** `[LoggerMessage]` throughout both apps; mapped 4xx outcomes logged at
  Information without stack traces; `SessionRemoval` logs shortened session ids; CSP reports
  are sanitised and capped before logging. No PII (emails, tokens, names) found in any log
  template.
- **Auth plumbing in the Web app.** `PostgresTokenStore` serialises refreshes with
  `FOR UPDATE`; `InMemoryTokenStore` is thread-safe; `OnValidatePrincipal` rejects a
  `blog_sid` with no store entry; neither `IHttpContextAccessor` nor
  `AuthenticationStateProvider` is used inside an `HttpClient` handler (the convention in
  `AGENTS.md`). `UserAccessTokenService` shares one refresh per session on purpose and
  documents why the refresh is not cancelled.
- **Security headers and CSP contents.** `SecurityHeadersMiddleware` assigns rather than
  appends; `StaticDocumentAntiforgery` wraps the framework's antiforgery; the static reading
  pages carry no inline style and no MudBlazor; `StaticAssetIntegrity` is a `Lazy<>` per
  asset. (The cost finding I-4 is about how often the header is built, not what is in it.)
- **Branding and site files.** `BrandingService.Load` and `SiteFilesService.Load` run once
  at startup, validate every field with a named error, hash files once and keep
  `FrozenDictionary`/`FrozenSet` snapshots. `SiteFile` bytes are served with a strong ETag.
  `WebFingerDocument.Matches` compares `acct:` host case-insensitively and the local part
  exactly, as RFC 7033 wants.
- **Blazor rules.** The seven reading pages carry `[ExcludeFromInteractiveRouting]`, load in
  `OnParametersSetAsync` with a superseded-load guard, use `Navigation.NotFound()` for real
  404s and have no event handlers. `App.razor` picks the render mode once. Components take
  `[Parameter]`s and report via `EventCallback`; the two components that inject
  `IBlogApiClient` (`MediaGrid`, `PostsList`) are the ones the spec marks as service
  components. `MarkdownEditor`'s W-009 handling of the bound value is correct for the
  problem it describes. `DraftReviewPage` disposes its `DotNetObjectReference` and guards
  interop during circuit teardown.
- **Nullable annotations.** Consistent across all three projects; the build is
  warning-free with nullable enabled; no `!` suppressions on values that can be null at that
  point (the `ReadFromJsonAsync(...)!` calls in `BlogApiClient` follow a successful status
  check).
- **Sync-over-async and fire-and-forget.** No `.Result`, `.Wait()`, `GetAwaiter().GetResult()`
  or `async void` in `src/`. The only un-awaited task is the deliberate shared refresh in
  `UserAccessTokenService` (M-15 is about observing it, not about its existence).
- **DI lifetimes.** No scoped service is captured by a singleton. Singletons
  (`UserAccessTokenService`, `MemoryPublicReadCache`, `CacheNotifier`, `FeedService`,
  `SitemapService`, `StaticAssetIntegrity`) depend only on singletons, options and
  `IHttpClientFactory`. `BlogApiClient` and `SessionExpiryHooks` are scoped and resolved
  from components, never from handlers.
- **Migrations.** The expand/contract guard test and `[ContractMigration]` attribute are in
  place; the pending contract (`Posts.ShowInNavigation`) is listed in `AGENTS.md`.

## Not verified here

- `tests/Croft.Api.Tests` (Testcontainers Postgres) could not run: no container runtime in
  the review container. Nothing above depends on an API test result, but the EF findings
  (I-6, I-9, M-8) would be confirmed by the SQL EF logs those tests produce.
- Whether `ASPNETCORE_FORWARDEDHEADERS_ENABLED` is set in the deployed environment (M-17).
- Whether the .NET 10 `IExceptionHandler` pipeline filters aborted-request cancellations
  before `ApiExceptionHandler` sees them (M-13).
- The actual per-request cost of I-4 and I-5 was reasoned from the code, not measured;
  `dev/` has no benchmark for the Web process. A `BenchmarkDotNet` run of
  `CspMiddleware.InvokeAsync` and `MarkdownRenderer.Render` on a 60K post would put numbers on
  both.
