# Brief 09c: public pages load their data once, not twice

Read AGENTS.md first and follow it, including the new "Interactivity" section, and load the skills
`writing-dotnet10-code` and `blazor-support-prerendering` before writing code. Implement inline
(`executing-plans`), one commit per item, TDD, break-it-once for each key test, E2E twice at the
end, the final whole-branch review. Brief 09b runs at the same time and also touches
`src/Croft.Web/Program.cs` and the Web tests; add to shared files, don't restructure them, and when
the orchestrator says 09b was accepted, `git fetch origin` and `git merge --no-edit
origin/blog-framework` (never rebase).

**Owner-visible outcome:** a reader's first page load costs Croft one round of API calls instead of
two. Today every page prerenders on the server and then runs `OnInitializedAsync` /
`OnParametersSetAsync` again when the Blazor circuit connects, fetching the same data from the API
a second time (AGENTS.md, "Interactivity"). With `[PersistentState]` the prerendered data travels
with the page and the interactive render reuses it.

**Evidence:** the 2026-10-14 load test broke at ~50 requests/s on CPU, with croft-api at 1.2–1.7
cores per replica and the database primary at 1.4 cores; that test sent plain HTTP (prerender
only), so real browsers cost about twice the API work it measured.

**No new packages.** Everything here ships in ASP.NET Core 10 and bUnit 2.11.3 (already
referenced).

## Item 1: public pages persist their prerendered data

**Now:** these load data from the API in a lifecycle method, and do it again after prerender:

| File | Calls |
|---|---|
| `Components/Pages/Home.razor` | `GetPostsAsync` |
| `Components/Pages/Post.razor` | `GetPostAsync`, `GetRelatedPostsAsync` |
| `Components/Pages/Category.razor` | `GetCategoryBySlugAsync`, `GetPostsAsync` |
| `Components/Pages/Tag.razor` | `GetTagBySlugAsync`, `GetPostsAsync` |
| `Components/Pages/Categories.razor` | `GetCategoriesAsync` |
| `Components/Pages/StaticPage.razor` | `GetPageAsync` |
| `Components/Pages/Search.razor` | `SearchAsync` (when there is a query) |
| `Components/Layout/MainLayout.razor` | `GetTopCategoriesAsync`, `GetPageNavigationAsync` |

**Do:** persist each page's loaded data with `[PersistentState]` (skill `blazor-support-prerendering`)
and skip the fetch when restored data is present.

**The trap, which decides correctness:** the public pages load in `OnParametersSetAsync` because
the same component instance is reused when the reader navigates from one post (or category, tag,
page, search query, page number) to another. A plain `Post ??= await ...` would keep showing the
**first** post after navigating to a second one. So the restored data must be used only when it
belongs to the current parameters: persist the key it was loaded for (slug, page number, query)
together with the data (one small record per page is fine), and fetch whenever the key differs
from the current parameters. Restored data is only ever available on the first render after a full
page load. Check how .NET 10's `[PersistentState]` behaves on enhanced navigation and parameter
changes (its `RestoreBehavior` / `AllowUpdates` options) against the docs and by test, and record
what you found in the commit.

Admin and editor pages are out of scope (they are not on the reader path).

**Tests** (bUnit, `tests/Croft.Web.Tests`; use bUnit's persistent-component-state support to
supply restored state, and a stub `IBlogApiClient` that counts calls; each test creates its own
data):
- For each page in the table: rendered with restored state for its current parameters → **0** API
  calls, and the restored content is what renders (assert a title or item the test chose).
- Without restored state → exactly the calls in the table, once each.
- **Parameter change:** Post rendered with restored state for slug A, then parameter set to slug B →
  `GetPostAsync` called once with B and B's title renders, not A's. The same for Category, Tag,
  StaticPage, Search (query), Home (page number).
- MainLayout with restored state → 0 calls; without → one call each.
- Break-it-once: in Post, use the restored data without comparing its slug; the slug-B test must
  fail.

**E2E** (`tests/Croft.Web.E2E`, outcome-based): open post A by full page load, wait for the
circuit, click a link to post B (client-side navigation): B's title and body show. Same from one
category to another. Break-it-once: the same revert as above makes the E2E fail.

**Evidence in the commit:** API requests for one full page load of `/post/<slug>` before and after
(count them in the E2E instance's API log, or any other way you can show; no test-only code in
`src/`).

## Item 2: work that only makes sense interactively waits for interactivity

**Do:** where a public page or the layout starts work that is pointless during prerender (JS
module imports, timers, anything needing the circuit), guard it with `RendererInfo.IsInteractive`
or move it to `OnAfterRenderAsync(firstRender)`, per the skill. List each place you changed, or
state that none needed it, in the commit.

**Tests:** for each change, a bUnit test that the work does not run when rendered statically (bUnit
lets you set the renderer info) and does run interactively. Break-it-once on one of them.

## Item 3: preload the framework's assets

**Do:** add `<ResourcePreloader />` to `Components/App.razor`'s `<head>` (ASP.NET Core 10; works
with `MapStaticAssets`, which Web already uses), so the browser starts fetching `blazor.web.js` and
its dependencies before it parses the body.

**Tests:** a Web integration test (`WebApplicationFactory`): `GET /` returns HTML whose head
contains the preload links the component emits for the fingerprinted framework script (assert the
`rel` and that the `href` matches the `<script>` the page actually loads). Break-it-once: remove the
component; the test fails.

## Out of scope

- Static SSR for public pages (a design decision for its own spec), and admin/editor pages.
- Any change to what a page shows or to the API; the rendered HTML is the same before and after.
- `AddValidation()`: Web's minimal endpoints parse their own bodies (the analytics beacon is
  `text/plain`), so there is nothing for it to validate here yet.
