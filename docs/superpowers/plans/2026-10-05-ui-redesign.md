# UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.
> Each **Part** below is delivered by one brief (`docs/briefs/R1-…` to `R5-…`). Implement only the Part your
> brief names; pass THIS file and the Part number to the skill — do not rewrite it into a new plan.

**Goal:** Replace the default MudBlazor look with the agreed "Highland classic" design: responsive sidebar
shell, featured+list home page, post page with TOC/tags/related rail, Crawford-tartan fallbacks, and an admin
area that can run the blog without typing URLs or IDs.

**Architecture:** Own CSS-grid layout shell (correct on first paint, no JS needed for defaults) wrapping
MudBlazor components themed from one set of CSS custom properties. Pages fetch through `IBlogApiClient`;
presentational components take `[Parameter]`s and raise `EventCallback`s. API gains summary DTOs, single
category per post, and the endpoints the admin UI needs.

**Tech Stack:** .NET 10 (SDK 10.0.4xx, `global.json`), ASP.NET Core Web API + EF Core 10 / Npgsql 10.0.3,
Blazor Server (InteractiveServer) + MudBlazor 9.11.0, Markdig 1.4.0, xUnit 2.9.3 + FluentAssertions 8.11.0,
bUnit 2.11.3 (new), Playwright 1.63 (NUnit), Deque.AxeCore.Playwright (new, latest stable).

**Spec:** `docs/superpowers/specs/2026-10-04-ui-redesign-design.md` (read it with this plan; section numbers
below refer to it). Assets: `docs/superpowers/specs/2026-10-04-ui-redesign/` (two remaining mockups as
`.html`; the logo, tartan, icons, mockup screenshots and the other mockups were removed before
publication: site branding, image of unknown provenance).

## Global Constraints

- Colour tokens exactly as spec §3.1 (light/dark values verbatim); crimson = act/current, green = classify/state only.
- Fonts self-hosted WOFF2 in `src/Blog.Web/wwwroot/fonts/`: Source Serif 4 (400, 400i, 600, 700), Source Sans 3 (400, 600), JetBrains Mono (400); `font-display: swap`. **No requests to fonts.googleapis.com** or any other third-party host from any page.
- Breakpoints: phone `< 960px`, rail `960–1279px`, full `≥ 1280px`; sidebar 260px, rail 64px, phone top bar 56px.
- Post rail visible only when the **content area** is `≥ 1000px` (CSS container query, not a media query); text column `max-width: 68ch`.
- Page size 10 on home/list pages; home page 1 = 1 Featured + 9 List; pagination via `?page=N`.
- Summary ≤ 300 chars (stored max 320), reading time `ceil(words/220)`, min 1; Subtitle ≤ 300; AltText ≤ 300; Category.Icon max 64, `^[A-Za-z0-9]+$`.
- Theme storage key `blog-theme` (`light|dark`, absent = system); sidebar key `blog-sidebar` (`collapsed`, ≥1280 only).
- Defaults (sidebar state, system theme) must render correctly **with JavaScript disabled**.
- Existing API query names stay: `searchTerm`, `tagSlug`, `categorySlug`, `status`, `page`, `pageSize` (the spec's `q/category/tag` map onto these; do not rename).
- Component names, folders and parameters exactly as spec §7. Pages fetch, components present (AGENTS.md conventions).
- Brief 04's page-view beacon in `Post.razor` (JS `sendBeacon` in `OnAfterRenderAsync(firstRender)`) must keep working after the post page is rebuilt.

## Review Focus

1. **Missing optional data** — a post with no category, no banner, no subtitle, empty summary, or no tags must render cleanly everywhere (cards, hero, rail, sidebar counts): no "null", no empty chip, no broken `<img>`. Owned by R3 (Task R3.2 tests) and R1 (Task R1.2 summary empty case).
2. **Long text** — 300-char titles/subtitles, 20 tags, a category name of 60 chars: cards clamp/ellipsize, the sidebar truncates with tooltip, nothing overflows horizontally at 390px. Owned by R3 (Task R3.2 bUnit + Task R3.6 E2E overflow check) and R2 (Task R2.4).
3. **Headings edge cases** — post with 0/1 headings (no TOC), duplicate heading texts (unique anchor ids), headings containing code/links (plain text in TOC). Owned by R3 (Task R3.4 tests).
4. **Unknown or removed icon names** in the database render the Folder fallback, never an exception. Owned by R2 (Task R2.3 test).
5. **Empty states** — no published posts, a category with no published posts (hidden from sidebar; its page shows "No posts yet"), search with no results, media library empty. Owned by R3 (Task R3.6) and R4 (Task R4.4).

---

## File Structure (new or substantially changed)

```
src/Blog.Shared/Contracts/
  PostSummaryDto.cs          BannerRefDto.cs          (new)
  PostDto.cs  CategoryDto.cs  MediaAssetDto.cs  CreatePostRequest.cs  UpdatePostRequest.cs   (changed)
  UpdateMediaRequest.cs  UpdateCategoryRequest.cs  UpdateTagRequest.cs                        (new)
src/Blog.Api/
  Models/Post.cs Category.cs MediaAsset.cs                                                    (changed)
  Data/Configurations/PostConfiguration.cs CategoryConfiguration.cs MediaAssetConfiguration.cs (changed)
  Migrations/<timestamp>_UiRedesignModel.cs                                                    (new)
  Services/PostSummaryExtractor.cs  PostSummaryBackfillService.cs  RelatedPostsQuery (in PostService) (new)
  Services/PostService.cs TagService.cs CategoryService.cs MediaService.cs FeedbackService.cs SearchService.cs (changed)
  Controllers/PostsController.cs MediaController.cs CategoriesController.cs TagsController.cs FeedbackController.cs (changed)
src/Blog.Web/
  wwwroot/app.css (tokens, fonts, shell grid)   wwwroot/fonts/*   wwwroot/js/shell.js   wwwroot/js/post.js
  wwwroot/favicon.ico  icon.svg  apple-touch-icon.png  manifest.webmanifest   wwwroot/img/crawford-tartan.svg
  wwwroot/lib/highlightjs/*
  Theme/BlogTheme.cs  Options/BlogOptions.cs  Services/IIconCatalog.cs IconCatalog.cs
  Services/IMarkdownRenderer.cs MarkdownRenderer.cs (returns html + headings)
  Components/Layout/   MainLayout, SidebarNav, BrandBlock, NavItem, TopBar, ThemeSwitch, SkipLink (+ .razor.css)
  Components/Blog/     PostCard, PostList, BannerOrTartan, CategoryChip, TagPills, PostMeta, PostHero, PostBody,
                       PostRail, TableOfContents, RelatedPosts, PagePager, NotFoundContent (+ .razor.css)
  Components/Admin/    StatusBadge, PostsGrid, PostEditorForm, MarkdownEditor, BannerPicker, MediaGrid,
                       MediaPickerDialog, IconPicker, IconPickerDialog, CategoryIcon, FeedbackPanel, ViewsChart
  Components/Pages/    Home, Category, Tag, Search, Post, NotFound (rewritten)
  Components/Pages/Admin/   PostsPage, PostEditPage, PostAnalyticsPage, MediaPage, TagsPage (new; old pages deleted)
  Components/Pages/Editor/  DraftsPage, DraftReviewPage (new; old pages deleted)
tests/Blog.Web.Tests/   Components/*Tests.cs (bUnit, new)   IconCatalogTests.cs  MarkdownRendererTests.cs
tests/Blog.Web.E2E/     LayoutTests.cs ThemeTests.cs HomeTests.cs PostPageTests.cs AdminTests.cs EditorTests.cs
                        AccessibilityTests.cs ScreenshotTests.cs  (existing tests' selectors updated)
```

---

## Part R1 — Data model and API (brief `R1-data-and-api.md`)

Backend only; no UI changes except keeping `Blog.Web` compiling against the changed contracts (minimal
mechanical adaptation of existing pages: show the single category where they showed the list).

### Task R1.1: Model, configuration and migration

**Files:**
- Modify: `src/Blog.Api/Models/Post.cs`, `Category.cs`, `MediaAsset.cs`
- Modify: `src/Blog.Api/Data/Configurations/PostConfiguration.cs`, `CategoryConfiguration.cs`, `MediaAssetConfiguration.cs`
- Create: `src/Blog.Api/Migrations/<timestamp>_UiRedesignModel.cs` (generated, then edited)
- Test: `tests/Blog.Api.Tests/MigrationTests.cs` (new)

**Interfaces — Produces:**
`Post.Subtitle (string?)`, `Post.CategoryId (Guid?)`, `Post.Category (Category?)`, `Post.Summary (string?)`,
`Post.ReadingMinutes (int)`; `Category.Icon (string?)`, `Category.Posts` (now one-to-many);
`MediaAsset.AltText (string?)`. `Post.Categories` is **removed**.

- [ ] **Step 1: Write the failing migration test.** It applies all migrations up to (but excluding) the new
  one, seeds a post linked to two categories through `CategoryPost`, then applies the new migration and
  asserts the post's `CategoryId` is one of the two (the lower `CategoriesId` for determinism) and the
  `CategoryPost` table no longer exists.

```csharp
[Fact]
public async Task UiRedesignMigration_KeepsFirstCategory_AndDropsJoinTable()
{
    var cs = await _fixture.CreateIsolatedConnectionStringAsync();
    await using var db = NewContext(cs);
    var migrator = db.GetService<IMigrator>();
    var all = db.Database.GetMigrations().ToList();
    var target = all.Single(m => m.EndsWith("_UiRedesignModel"));
    await migrator.MigrateAsync(all[all.IndexOf(target) - 1]);

    var c1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var c2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var post = Guid.NewGuid();
    await db.Database.ExecuteSqlRawAsync($"""
        INSERT INTO "Categories" ("Id","Name","Slug") VALUES ('{c2}','B','b'), ('{c1}','A','a');
        INSERT INTO "Posts" ("Id","Slug","Title","MarkdownContent","Status","CreatedAt","UpdatedAt","AuthorId","AuthorName","ViewCount")
          VALUES ('{post}','p','P','x',0,now(),now(),'a','A',0);
        INSERT INTO "CategoryPost" ("CategoriesId","PostsId") VALUES ('{c2}','{post}'), ('{c1}','{post}');
        """);

    await migrator.MigrateAsync(target);

    var categoryId = await db.Database.SqlQueryRaw<Guid?>(
        $"""SELECT "CategoryId" AS "Value" FROM "Posts" WHERE "Id" = '{post}'""").SingleAsync();
    categoryId.Should().Be(c1);
    var joinExists = await db.Database.SqlQueryRaw<bool>(
        """SELECT to_regclass('"CategoryPost"') IS NOT NULL AS "Value" """).SingleAsync();
    joinExists.Should().BeFalse();
}
```

- [ ] **Step 2: Run it — FAIL** (`Sequence contains no matching element`: migration doesn't exist).
  `dotnet test tests/Blog.Api.Tests --no-build --filter "FullyQualifiedName~MigrationTests"` after `dotnet build`.
- [ ] **Step 3: Change the model and configurations.**
  - `PostConfiguration`: remove `HasMany(x => x.Categories).WithMany(x => x.Posts)`; add
    `HasOne(p => p.Category).WithMany(c => c.Posts).HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.SetNull);`
    `Property(p => p.Subtitle).HasMaxLength(300); Property(p => p.Summary).HasMaxLength(320); Property(p => p.ReadingMinutes).HasDefaultValue(1);`
  - `CategoryConfiguration`: `Property(c => c.Icon).HasMaxLength(64);`
  - `MediaAssetConfiguration`: `Property(m => m.AltText).HasMaxLength(300);`
- [ ] **Step 4: Generate and edit the migration.**
  `dotnet tool run dotnet-ef migrations add UiRedesignModel -p src/Blog.Api -s src/Blog.Api`.
  EF will emit `DropTable("CategoryPost")` **before** you can copy data. Edit `Up` so the order is:
  add `CategoryId`/`Subtitle`/`Summary`/`ReadingMinutes`/`Icon`/`AltText` columns → copy → drop join table → FK/index:

```csharp
migrationBuilder.Sql("""
    UPDATE "Posts" p SET "CategoryId" = (
        SELECT cp."CategoriesId" FROM "CategoryPost" cp
        WHERE cp."PostsId" = p."Id" ORDER BY cp."CategoriesId" LIMIT 1);
    """);
```

  `Down` recreates `CategoryPost` and copies `CategoryId` back into it before dropping the column.
- [ ] **Step 5: Run the test — PASS. Run all API tests** (existing ones referencing `Categories` will fail to
  compile until R1.3; do R1.1–R1.3 before the item's full-suite run, commit per task anyway with the red/green
  record in the message, per AGENTS.md rule 9).
- [ ] **Step 6: Commit** `-- src/Blog.Api tests/Blog.Api.Tests/MigrationTests.cs`.

### Task R1.2: Summary extraction, reading time, backfill

**Files:**
- Create: `src/Blog.Api/Services/PostSummaryExtractor.cs`, `src/Blog.Api/Services/PostSummaryBackfillService.cs`
- Modify: `src/Blog.Api/Services/PostService.cs` (Create/Update call the extractor), `src/Blog.Api/Program.cs` (register hosted service), `src/Blog.Api/Blog.Api.csproj` (add `Markdig` 1.4.0)
- Test: `tests/Blog.Api.Tests/PostSummaryExtractorTests.cs`, `tests/Blog.Api.Tests/PostSummaryBackfillTests.cs`

**Interfaces — Produces:** `static (string Summary, int ReadingMinutes) PostSummaryExtractor.Extract(string markdown)`.

- [ ] **Step 1: Failing tests** (xUnit `[Theory]` where it fits):

```csharp
[Fact] public void SkipsHeadingsAndUsesFirstParagraph() =>
    PostSummaryExtractor.Extract("# Title\n\nFirst *para* with [a link](x).\n\nSecond.").Summary
        .Should().Be("First para with a link.");
[Fact] public void SkipsImageOnlyParagraph() =>
    PostSummaryExtractor.Extract("![alt](i.png)\n\nReal text.").Summary.Should().Be("Real text.");
[Fact] public void SkipsCodeBlocks() =>
    PostSummaryExtractor.Extract("```cs\nvar x=1;\n```\n\nAfter code.").Summary.Should().Be("After code.");
[Fact] public void CollapsesWhitespace() =>
    PostSummaryExtractor.Extract("Line one\nline   two.").Summary.Should().Be("Line one line two.");
[Fact] public void TruncatesAtWordBoundaryWithEllipsis()
{
    var s = PostSummaryExtractor.Extract(string.Join(' ', Enumerable.Repeat("word", 100))).Summary;
    s.Length.Should().BeLessThanOrEqualTo(301); s.Should().EndWith("…"); s.Should().NotContain("wor…");
}
[Fact] public void EmptyWhenNoParagraph() => PostSummaryExtractor.Extract("# Only heading").Summary.Should().BeEmpty();
[Theory, InlineData(0, 1), InlineData(220, 1), InlineData(221, 2), InlineData(660, 3)]
public void ReadingMinutes(int words, int expected) =>
    PostSummaryExtractor.Extract(string.Join(' ', Enumerable.Repeat("w", words))).ReadingMinutes.Should().Be(expected);
[Fact] public void ReadingMinutesExcludesFencedCode() =>
    PostSummaryExtractor.Extract("```\n" + string.Join(' ', Enumerable.Repeat("w", 500)) + "\n```\n\nhi").ReadingMinutes.Should().Be(1);
```

- [ ] **Step 2: Run — FAIL** (type missing).
- [ ] **Step 3: Implement** with Markdig's AST (`Markdown.Parse(markdown, pipeline)` with
  `UseAdvancedExtensions()`): walk top-level `ParagraphBlock`s, skip those whose inlines are only
  `LinkInline { IsImage: true }` plus whitespace; render inline text by concatenating `LiteralInline`,
  `CodeInline.Content`, and the text inside `LinkInline`/`EmphasisInline` children (no markup); collapse
  `\s+` to one space; if `> 300` chars, cut at the last space at or before 300 and append `…`. Word count:
  plain text of all non-`FencedCodeBlock`/`CodeBlock` blocks split on whitespace.
- [ ] **Step 4: Run — PASS.** Then backfill:
  test `Backfill_FillsNullSummaries_AndIsIdempotent` — insert two posts with `Summary = NULL` via SQL, run
  `PostSummaryBackfillService.RunOnceAsync`, assert both filled; run again, assert `UpdatedAt` unchanged.
- [ ] **Step 5: Implement `PostSummaryBackfillService : BackgroundService`** whose `ExecuteAsync` calls
  `RunOnceAsync` once at startup (scoped `BlogDbContext`; processes `Summary == null` in batches of 100;
  never touches `UpdatedAt`). `PostService.CreateAsync/UpdateAsync` set `Summary`/`ReadingMinutes` from the extractor.
- [ ] **Step 6: Run — PASS; commit.**

### Task R1.3: Contracts and post endpoints

**Files:**
- Create: `src/Blog.Shared/Contracts/PostSummaryDto.cs`, `BannerRefDto.cs`
- Modify: `PostDto.cs`, `CategoryDto.cs`, `MediaAssetDto.cs`, `CreatePostRequest.cs`, `UpdatePostRequest.cs`
- Modify: `src/Blog.Api/Services/IPostService.cs`, `PostService.cs`, `SearchService.cs`, `ISearchService.cs`, `Controllers/PostsController.cs`, `Controllers/SearchController.cs`
- Modify (compile only): `src/Blog.Web/Services/IBlogApiClient.cs`, `BlogApiClient.cs`, existing pages using `post.Categories`
- Test: `tests/Blog.Api.Tests/PostsControllerTests.cs`, `SearchControllerTests.cs` (extend)

**Interfaces — Produces (exact):**

```csharp
public record BannerRefDto(string Url, string? AltText);
public record PostSummaryDto(Guid Id, string Slug, string Title, string? Subtitle, string Summary,
    int ReadingMinutes, PostStatus Status, DateTime? PublishedAt, CategoryDto? Category,
    IReadOnlyList<TagDto> Tags, BannerRefDto? Banner, long? ViewCount, int? OpenFeedbackCount);
public record CategoryDto(Guid Id, string Name, string Slug, string? Icon, int PostCount);
public record PostDto(Guid Id, string Slug, string Title, string? Subtitle, string MarkdownContent,
    string? Excerpt, PostStatus Status, DateTime? PublishedAt, DateTime CreatedAt, DateTime UpdatedAt,
    string AuthorId, string AuthorName, MediaAssetDto? BannerImage, IReadOnlyList<TagDto> Tags,
    CategoryDto? Category, int ReadingMinutes);
public record MediaAssetDto(Guid Id, string FileName, string OriginalName, string ContentType, string Url,
    MediaAssetScope Scope, Guid? PostId, string? AltText);
public record CreatePostRequest(string Title, string MarkdownContent, string? Subtitle = null,
    string? Excerpt = null, Guid? BannerImageId = null, IReadOnlyList<string>? Tags = null, Guid? CategoryId = null);
public record UpdatePostRequest(string Title, string MarkdownContent, string? Subtitle = null,
    string? Excerpt = null, Guid? BannerImageId = null, IReadOnlyList<string>? Tags = null, Guid? CategoryId = null);
```

`IPostService.ListAsync` returns `PagedResult<PostSummaryDto>` and takes `bool includeAdminCounts`;
`ISearchService.SearchAsync` returns `PagedResult<PostSummaryDto>`.

- [ ] **Step 1: Failing tests** (API integration, existing fixture):
  `List_ReturnsSummaries_WithoutMarkdown` (assert raw JSON has no `markdownContent` property);
  `List_AdminCounts_NullForAnonymous_SetForAdmin` (anonymous → `viewCount`/`openFeedbackCount` null; admin → numbers, open feedback = unresolved only);
  `Create_WithSubtitleAndCategoryId_RoundTrips`;
  `Create_SubtitleOver300_Returns400`;
  `Create_UnknownCategoryId_Returns400` (`ValidationException`);
  `Search_ReturnsSummaries`.
- [ ] **Step 2: Run — FAIL.**
- [ ] **Step 3: Implement.** `PostService` builds summaries with a projection (`Select(p => new PostSummaryDto(...))`)
  — no `Include` of `MarkdownContent`; `OpenFeedbackCount = p.Feedback.Count(f => f.ResolvedAt == null)`,
  `ViewCount = p.ViewCount`, both only when `includeAdminCounts` (controller passes `User.IsInRole("Admin")`).
  `CategoryDto.PostCount` here = published posts in that category. Validation through the existing
  domain exceptions (`ValidationException` → 400). Adapt `BlogApiClient`/pages mechanically (show `post.Category?.Name`).
- [ ] **Step 4: Run all suites — PASS; commit.**

### Task R1.4: Related posts, unpublish, resolve feedback

**Files:** `PostService.cs`, `IPostService.cs`, `PostsController.cs`, `FeedbackService.cs`, `IFeedbackService.cs`, `FeedbackController.cs`; tests in `PostsControllerTests.cs`, `FeedbackControllerTests.cs`.

**Interfaces — Produces:** `GET /api/posts/{slug}/related?count=3` → `IReadOnlyList<PostSummaryDto>`;
`POST /api/posts/{id}/unpublish` (Admin) → `PostDto`; `POST /api/posts/{postId}/feedback/{feedbackId}/resolve` (Admin) → `FeedbackDto`.

- [ ] **Step 1: Failing tests:**
  `Related_RanksBySharedTagsThenCategoryThenNewest` (seed: A tags {x,y} cat C; B tags {x,y} → score 4;
  C cat C no tags → 1; D newest unrelated → 0; ask for A, count 3 → [B, C, D]);
  `Related_ExcludesSelfAndUnpublished`; `Related_CountClampedTo1to10`;
  `Unpublish_SetsDraft_ClearsPublishedAt_DisappearsFromPublicList`; `Unpublish_RequiresAdmin` (Editor → 403);
  `Resolve_SetsResolvedAt`; `Resolve_FeedbackOnOtherPost_Returns404`.
- [ ] **Step 2: Run — FAIL. Step 3: Implement** (score in SQL: `2 * p.Tags.Count(t => tagIds.Contains(t.Id)) + (p.CategoryId == categoryId ? 1 : 0)`,
  order by score desc then `PublishedAt` desc; filler = newest published not already chosen). **Step 4: PASS; commit.**

### Task R1.5: Media alt text and delete; category/tag edit and delete

**Files:** `MediaService.cs`, `IMediaService.cs`, `MediaController.cs`, `CategoryService.cs`, `ICategoryService.cs`, `CategoriesController.cs`, `TagService.cs`, `ITagService.cs`, `TagsController.cs`; contracts `UpdateMediaRequest(string? AltText)`, `UpdateCategoryRequest(string Name, string? Icon)`, `UpdateTagRequest(string Name)`.

- [ ] **Step 1: Failing tests:**
  `UpdateMedia_SetsAltText` / `UpdateMedia_AltTextOver300_Returns400`;
  `DeleteMedia_RemovesRowAndFile`; `DeleteMedia_UsedAsBanner_Returns409_WithPostTitles` (ProblemDetails `detail` lists titles — this 409 is a domain conflict, so give it its own `ConflictException` mapped to 409 **with** the message; the generic 23505 mapping keeps its generic text);
  `UpdateCategory_SetsNameAndIcon` / `UpdateCategory_InvalidIcon_Returns400` (`"bad icon!"`);
  `DeleteCategory_InUse_Returns409` / `DeleteCategory_Unused_Returns204`;
  `RenameTag` / `DeleteTag_InUse_Returns409`;
  `ListCategories_IncludesIconAndPublishedCount`.
- [ ] **Step 2: FAIL. Step 3: Implement. Step 4: PASS; full suites; commit.**

---

## Part R2 — Visual foundation and shell (brief `R2-foundation-and-shell.md`)

### Task R2.1: Assets, fonts, tokens, theme

**Files:**
- Create: `src/Blog.Web/wwwroot/fonts/*.woff2` + `fonts.css` section in `app.css`; `wwwroot/icon.svg` (= spec asset `logo.svg`), `favicon.ico`, `apple-touch-icon.png`, `manifest.webmanifest`, `wwwroot/img/crawford-tartan.svg`; delete `favicon.png`
- Modify: `src/Blog.Web/wwwroot/app.css` (tokens §3.1 for `:root`, `:root[data-theme=dark]`, and `@media (prefers-color-scheme: dark) { :root:not([data-theme=light]) {…} }`)
- Create: `src/Blog.Web/Theme/BlogTheme.cs` (`MudTheme` built from the same values), `src/Blog.Web/Options/BlogOptions.cs` (`Name`, `Tagline`, bound from `Blog`)
- Modify: `src/Blog.Web/Components/App.razor` (head: font preloads, icons, manifest, the inline theme/sidebar script below, `<html lang="en">`), `appsettings.json` (`"Blog": {"Name": "crawford.dev", "Tagline": "Code, travel and the odd loaf"}`)
- Test: `tests/Blog.Web.E2E/ThemeTests.cs`

Fonts: download the official WOFF2 releases (Source Serif 4 and Source Sans 3 from github.com/adobe-fonts, JetBrains Mono from github.com/JetBrains/JetBrainsMono), subset not required; commit the files and their OFL licence texts to `wwwroot/fonts/`.

Inline script (the **only** inline script; place first in `<head>`):

```html
<script>
(function () {
  try {
    var t = localStorage.getItem('blog-theme');
    if (t === 'light' || t === 'dark') document.documentElement.setAttribute('data-theme', t);
    if (localStorage.getItem('blog-sidebar') === 'collapsed') document.documentElement.classList.add('sidebar-collapsed');
  } catch (e) { /* storage blocked: defaults apply */ }
})();
</script>
```

- [ ] **Step 1: Failing E2E** `ThemeTests`:
  `NoJs_SystemDark_RendersDarkBackground` (new browser context with `JavaScriptEnabled = false`,
  `ColorScheme = ColorScheme.Dark`; take a screenshot clipped to 1×1 px at a point inside `main` that is page
  background, decode it with a small helper `PixelAt(byte[] png)` (PNG IDAT via `System.IO.Compression.ZLibStream`,
  colour type 2 or 6, filter byte 0) and assert it equals `#171314` ±2 per channel. Screenshots work with page
  JavaScript disabled; `page.EvaluateAsync` does not.)
  `ThemeSwitch_PersistsLight_AcrossReload_EvenWhenSystemDark` (JS on, dark scheme, click switch until "Light", reload, background `#FAF7F4`);
  `NoThirdPartyRequests` (record all request URLs on Home and Post; assert every host is `localhost`).
- [ ] **Step 2: FAIL. Step 3: Implement. Step 4: PASS; commit.**

### Task R2.2: Layout shell (CSS grid, sidebar states, JS module)

**Files:**
- Modify: `Components/Layout/MainLayout.razor` (+ `.razor.css`); Create: `SidebarNav`, `BrandBlock`, `NavItem`, `TopBar`, `SkipLink`, `ThemeSwitch` (+ `.razor.css` each); Delete: `NavMenu.razor(.css)`
- Create: `wwwroot/js/shell.js` (ES module: `init()`, toggles, Esc/scrim, focus trap, `blog-sidebar` save, theme cycling)
- Test: `tests/Blog.Web.E2E/LayoutTests.cs`, `tests/Blog.Web.Tests/Components/SidebarNavTests.cs`

Shell CSS (in `app.css`, global because it styles `<html>` classes):

```css
.shell { display: grid; grid-template-columns: var(--sidebar-w) 1fr; min-height: 100vh; }
.shell > .sidebar { position: sticky; top: 0; height: 100vh; overflow-y: auto; }
.shell > main { container-type: inline-size; min-width: 0; }
:root { --sidebar-w: 260px; }
html.sidebar-collapsed { --sidebar-w: 64px; }
@media (min-width: 960px) and (max-width: 1279.98px) { :root { --sidebar-w: 64px; } }
@media (max-width: 959.98px) {
  .shell { grid-template-columns: 1fr; }
  .shell > .sidebar { position: fixed; inset: 0 auto 0 0; width: 280px; transform: translateX(-100%);
                      transition: transform .2s; z-index: 1300; }
  html.nav-open .shell > .sidebar { transform: none; }
  .topbar { display: flex; }
}
@media (min-width: 960px) { .topbar { display: none; } }
/* rail: hide labels/counts, keep icons, tooltips via title */
:root:is(.sidebar-collapsed) .sidebar .label, .sidebar.rail .label { display: none; }
@media (min-width: 960px) and (max-width: 1279.98px) { .sidebar .label { display: none; } html.nav-open .sidebar .label { display: inline; } }
```

(`SidebarNav` renders labels in `<span class="label">` and `title` attributes for rail tooltips; on 960–1279
`html.nav-open` widens the sidebar to 260px as an overlay with a scrim.)

- [ ] **Step 1: Failing tests.**
  First check whether Playwright locator queries (`BoundingBoxAsync`, `IsVisibleAsync`) work in a context with
  `JavaScriptEnabled = false`. If they do, use them; if not, use the 1×1-pixel screenshot helper from R2.1
  (sample a point inside the sidebar area and one just right of 64px/260px). If neither works, STOP and report
  (AGENTS.md rule 8) rather than testing with JavaScript on — "correct with JS disabled" is the requirement.
  E2E `LayoutTests` (all with **JS disabled**): at 1440×900 sidebar bounding box width 260 and "Programming"
  label visible; at 1100×800 width 64, labels hidden, logo visible; at 390×844 sidebar not in viewport
  (`x + width <= 0`) and top bar visible with ☰. With JS: at 390 ☰ opens (sidebar `x == 0`), clicking a
  category navigates and closes it, Esc closes it; at 1440 Collapse → 64px, reload → still 64px.
  bUnit `SidebarNavTests`: Admin section only for role Admin; Editor section only for Editor; categories with
  `PostCount == 0` not rendered; a 60-char category name gets `title` with the full name.
- [ ] **Step 2: FAIL. Step 3: Implement. Step 4: PASS. Break-it-once:** delete the `max-width: 959.98px` block → the 390px no-JS test must fail; revert. **Commit.**

### Task R2.3: Icon catalog and CategoryIcon

**Files:** Create `src/Blog.Web/Services/IIconCatalog.cs`, `IconCatalog.cs`, `Components/Admin/CategoryIcon.razor`; register singleton; tests `tests/Blog.Web.Tests/IconCatalogTests.cs`, `Components/CategoryIconTests.cs`.

**Interfaces — Produces:**

```csharp
public interface IIconCatalog
{
    IReadOnlyList<string> Names { get; }                 // all Icons.Material.Filled constants, sorted
    string Resolve(string? name);                         // SVG path data; unknown/empty -> Folder
    IReadOnlyList<string> Search(string query, int max = 200); // case-insensitive contains, prefix matches first
}
```

Implementation: `typeof(Icons.Material.Filled).GetFields(BindingFlags.Public | BindingFlags.Static)` where
`FieldType == typeof(string)`, cached in the constructor.

- [ ] **Steps:** tests `Resolve_Unknown_ReturnsFolder`, `Resolve_Null_ReturnsFolder`, `Resolve_Terrain_ReturnsTerrainPath`,
  `Search_Mount_IncludesTerrainOrLandscape_PrefixFirst`, `Names_HasHundreds` (> 1000); bUnit `CategoryIcon`
  renders `<svg>` with Folder path for `"NoSuchIcon"`. FAIL → implement → PASS → commit.

### Task R2.4: bUnit project setup

Fold into R2.2's first test: add `bunit` 2.11.3 to `tests/Blog.Web.Tests`, a `BunitContext` base registering
`MudServices` (`Services.AddMudServices()`, `JSInterop.Mode = JSRuntimeMode.Loose`), a fake `IIconCatalog`,
and `AddAuthorization()` with `this.AddAuthorization().SetRoles(...)` helpers.

---

## Part R3 — Public pages (brief `R3-public-pages.md`)

### Task R3.1: Markdown renderer with headings

**Files:** `Services/IMarkdownRenderer.cs`, `MarkdownRenderer.cs`; tests `MarkdownRendererTests.cs`.

**Interfaces — Produces:**

```csharp
public sealed record Heading(int Level, string Text, string Id);
public sealed record RenderedMarkdown(string Html, IReadOnlyList<Heading> Headings);
public interface IMarkdownRenderer { RenderedMarkdown Render(string markdown); }
```

(Remove `ToHtml`; update callers.) Keep `DisableHtml()`. Headings = `HeadingBlock`s with level 2–3, text
= plain inline text (code/link text included, no markup), `Id` = `block.GetAttributes().Id` (from
AutoIdentifiers; duplicates already get `-1`, `-2`).

- [ ] Tests: `Render_ReturnsH2AndH3Only`; `Render_DuplicateHeadings_GetUniqueIds`;
  `Render_HeadingWithCodeAndLink_TextIsPlain`; `Render_NoHeadings_EmptyList`; `Render_RawHtmlStillDisabled`.
  FAIL → implement → PASS → commit.

### Task R3.2: Card components

**Files:** `Components/Blog/BannerOrTartan`, `CategoryChip`, `TagPills`, `PostMeta`, `PostCard`, `PostList`, `PagePager` (+ `.razor.css`); bUnit tests per component.

Key rules (spec §5.1, §7.3): whole card is one `<a>` whose accessible name is the title; Featured image area
`aspect-ratio: 21/9; max-height: 420px` (phones `4/3`); List image 34% (min 96px); summary clamped with
`-webkit-line-clamp` (Featured 3, List 2); tartan fallback = `background: url(/img/crawford-tartan.svg) 0 0/180px`
under `--tartan-wash`, icon white 64px/36px; `PostMeta` renders "2 October 2026 · 7 min read" (en-GB date).

- [ ] bUnit tests: `PostCard_Featured_WithBanner_RendersImgWithAlt`; `PostCard_NoBanner_RendersTartanAndCategoryIcon`;
  `PostCard_NoCategory_NoChip_FolderIconInFallback`; `PostCard_NoSubtitle_NoEmptyElement`;
  `PostCard_EmptySummary_NoEmptyParagraph`; `PostCard_LinkNameIsTitle`;
  `PostList_ShowFeatured_FirstItemFeaturedRestList`; `PostList_NoFeatured_AllList`;
  `PostList_Empty_ShowsEmptyMessage` (parameter `EmptyText`, e.g. "Nothing published yet." / "No posts yet");
  `PagePager_BuildsPageQueryLinks_AndMarksCurrent`; `TagPills_LinksToTagPages`.
  FAIL → implement → PASS → commit.

### Task R3.3: Home, category, tag, search pages

**Files:** rewrite `Components/Pages/Home.razor`, `Category.razor`, `Tag.razor`, `Search.razor`; `IBlogApiClient`
list methods return `PagedResult<PostSummaryDto>`; `GetCategoriesAsync` returns the new `CategoryDto`.

- [ ] E2E (extend `HomePageTests` → `HomeTests`): page 1 has exactly one Featured card then List cards; `?page=2`
  has no Featured; a published post without banner shows the tartan fallback (assert computed
  `background-image` contains `crawford-tartan.svg`) and its category icon; unknown category slug → 404 page;
  a category with no published posts (create one via the API as admin) → its page shows "No posts yet" and it is
  absent from the sidebar; empty search → "No results" message. FAIL → implement → PASS → commit.

### Task R3.4: Post page (hero, body, rail, TOC, related, highlighting)

**Files:** `Components/Blog/PostHero`, `PostBody`, `PostRail`, `TableOfContents`, `RelatedPosts`; rewrite
`Components/Pages/Post.razor` (**keep brief 04's beacon call**); `wwwroot/js/post.js` (scroll-spy with
IntersectionObserver; `highlightAll(root)`); `wwwroot/lib/highlightjs/` (highlight.js core + languages
csharp, javascript, typescript, json, yaml, bash, sql, xml, css; Razor → xml if no maintained grammar) and
`wwwroot/css/highlight-theme.css` built from the tokens.

Rail via container query on `main` (spec §5.4):

```css
.post-layout { display: grid; grid-template-columns: minmax(0, 68ch); gap: 3rem; }
@container (min-width: 1000px) { .post-layout { grid-template-columns: minmax(0, 68ch) 220px; } .toc-inline { display: none; } }
@container (max-width: 999.98px) { .post-rail { display: contents; } .post-rail .toc { display: none; } }
```

- [ ] bUnit: `TableOfContents_HiddenWhenFewerThan2Headings`; `TableOfContents_LinksToIds`; `PostHero_NoBanner_Tartan`;
  `PostHero_DraftRibbon_WhenRequested`. E2E `PostPageTests`: at 1440 rail visible with TOC/tags/related; at 1100 with
  sidebar open, rail hidden and `.toc-inline` visible; clicking a TOC link scrolls the heading into view
  (`heading.BoundingBoxAsync().Y < 200`); code block has `hljs` class after render; page view beacon still
  increments the view count (reuse brief 04's helper). FAIL → implement → PASS → commit.

### Task R3.5: Not-found page with real 404

**Files:** `Components/Blog/NotFoundContent.razor`, `Components/Pages/NotFound.razor`, `Routes.razor`, post/category/tag pages call `NavigationManager.NotFound()`.

- [ ] E2E: `GET /post/does-not-exist` → response status 404 and text "This page has wandered off"; `GET /no-such-route` → 404.
  Break-it-once: make the post page `NavigateTo("/not-found")` instead → status test fails; revert. Commit.

### Task R3.6: Accessibility and overflow pass (public)

- [ ] Add `Deque.AxeCore.Playwright`; `AccessibilityTests` runs axe on Home, Post (light and dark) and fails on
  `serious`/`critical`. Add `NoHorizontalOverflow_At390` (Home, Post with a 300-char title and 20 tags:
  `document.documentElement.scrollWidth <= 390`). Fix what they find. Commit.

---

## Part R4 — Admin (brief `R4-admin.md`)

### Task R4.1: Posts list

**Files:** `Components/Admin/StatusBadge`, `PostsGrid`; `Components/Pages/Admin/PostsPage.razor` (`/admin/posts`, and `/admin` redirects here); delete `Pages/Admin/Analytics.razor`, `FeedbackReview.razor`.
- [ ] bUnit: `StatusBadge` colours per status (Published uses green container class, Scheduled amber, Draft neutral). E2E `AdminTests.PostsList_FilterByStatus_AndSearchByTitle`; row actions navigate to edit/analytics/feedback tab. Commit.

### Task R4.2: Post editor

**Files:** `Components/Admin/PostEditorForm`, `MarkdownEditor`, `BannerPicker`, `FeedbackPanel`; `Components/Pages/Admin/PostEditPage.razor` (`/admin/posts/new`, `/admin/posts/{id:guid}/edit`, `?tab=feedback`); delete `Pages/Admin/PostEditor.razor`; `wwwroot/js/editor.js` gains `insertAtCursor(textarea, text)`.
Behaviour per spec §6.2 (Publish now ignores date; Schedule requires future UTC date; Unpublish; every action saves first; UTC combine via existing `UtcDateTime.Combine`).
- [ ] bUnit: `MarkdownEditor_InsertImage_InsertsMarkdownAtCursor` (JS interop verified); `PostEditorForm_ScheduleDisabledForPastDate`; `FeedbackPanel_Resolve_RaisesCallback_AndMovesToResolved`.
  E2E: edit subtitle → save → public post shows it; Unpublish removes it from Home; Preview tab uses `PostHero`/`PostBody` (assert same classes as public page). Commit.

### Task R4.3: Media library and picker

**Files:** `Components/Admin/MediaGrid`, `MediaPickerDialog`; `Components/Pages/Admin/MediaPage.razor`; delete `Pages/Admin/MediaLibrary.razor`.
- [ ] E2E: multi-upload two PNGs → both tiles; set alt text → Copy Markdown yields `![alt](/api/media/{id})` (read clipboard via granted permission); delete banner image → refused with the post title in the message; editor "Insert from library" inserts the markdown. Empty library shows an empty state. Commit.

### Task R4.4: Tags & categories with icon picker; analytics page

**Files:** `Components/Admin/IconPicker`, `IconPickerDialog`, `ViewsChart`; `Pages/Admin/TagsPage.razor` (`/admin/tags`), `PostAnalyticsPage.razor` (`/admin/posts/{id:guid}/analytics`); delete `TagsAndCategories.razor`.
**API addition (found by R1's final review):** spec §6.5 shows a per-tag post count but `TagDto` has none. Change
`TagDto` to `TagDto(Guid Id, string Name, string Slug, int PostCount)` (published posts; same rule as
`CategoryDto.PostCount`), filled by `GET /api/tags` and wherever tags are embedded (use 0 inside post DTOs if
computing it there is costly — document which). API test `ListTags_IncludesPublishedPostCount` first.
- [ ] bUnit: `IconPicker_Search_Filters`, `IconPicker_SelectDefault_SetsNull`. E2E: choose "Terrain" for a category → sidebar shows that icon; category delete disabled while in use (tooltip text); analytics page shows total and a chart. Commit.

---

## Part R5 — Editor role, screenshots, final pass (brief `R5-editor-and-final.md`)

### Task R5.1: Editor pages

**Files:** `Pages/Editor/DraftsPage.razor` (`/editor`), `DraftReviewPage.razor` (`/editor/posts/{slug}`); delete `Drafts.razor`, `DraftReader.razor`. Review page = `PostHero DraftRibbon` + `PostBody` + `PostRail` with `FeedbackPanel CanAdd` in `ChildContent`.
- [ ] E2E `EditorTests`: editor selects text → "Add feedback" → note appears in rail; admin resolves it in the editor's Feedback tab → it shows under "Resolved". Commit.

### Task R5.2: Screenshots and full accessibility

- [ ] `ScreenshotTests` (marked `[Category("Screenshots")]`, not asserted): Home, Post, Posts list, Editor at 1440/1100/390 in light and dark to `TestContext.CurrentContext.WorkDirectory/screenshots/`. Extend `AccessibilityTests` to the Editor and Posts list. Run the whole E2E suite twice in a row (no flakes). Commit.

### Task R5.3: Cleanup

- [ ] Remove dead code (old `editor.js` functions, unused CSS, `Pages/Error.razor` restyled with the shell), make sure no page still references removed components, `dotnet build` warning-free. Update AGENTS.md "Conventions" for the new component folders if anything changed. Commit.
