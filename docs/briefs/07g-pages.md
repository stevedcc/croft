# Brief 07g: pages, written like posts, shown at the root and in the sidebar

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review. Starts after brief 07e is accepted; brief 07f
(CSP) may be running: it owns `MainLayout.razor`'s `MudThemeProvider` line and `App.razor`; you
add one parameter to `MainLayout`'s `SidebarNav` call and nothing else there. When the
orchestrator says 07f was accepted, `git fetch origin` and `git merge --no-edit
origin/blog-framework` (never rebase), keep both sides, re-run the suites, and add a page to
`CspTests`' page list (light and dark) so pages are held to the public policy too.

**Owner-visible outcome:** in the admin, "New page" opens the same editor as a post. A published
page lives at `/<slug>` (for example `/about`), has a title header with its dates and the Markdown
body in the reading column, and, if marked, appears in the sidebar under Home and Search, above
Categories. Pages never appear among posts: not on the home page, category and tag pages, search,
Related, or the feed. The first page is the owner's About page; they write it.

**Facts (checked 2026-10-11):** `Post` (`Croft.Api/Models/Post.cs`) has `Slug`, `Status`,
`PublishedAt`, `UpdatedAt`, `CategoryId`, `Tags`, `BannerImageId`. Public post routes are
`/post/{slug}`; `NotFound.razor` has the catch-all `/{**path:nonfile}`. The sidebar
(`Components/Layout/SidebarNav.razor`) gets its categories from `MainLayout`
(`Api.GetTopCategoriesAsync`). `UpdatedAt` reaches the page only as SEO metadata
(`Post.razor:17`); no reader sees an "updated" date today. Web endpoints at the root:
`/robots.txt`, `/sitemap.xml`, `/feed.xml`, `/manifest.webmanifest`, `/branding/*`, `/login`,
`/logout`, `/backchannel-logout`, `/api/*`, plus OIDC's `/signin-oidc` and
`/signout-callback-oidc`, and 07f adds `/csp-report`.

## Item 1: a page is a kind of post (API, data, validation)

- `PostKind { Post, Page }` (`Croft.Shared.Enums`), `Post.Kind` (default `Post`), and for pages
  `ShowInNavigation` (bool) and `NavigationOrder` (int, default 0); one migration; existing rows
  are posts. EF reports no pending model changes afterwards.
- Create/update requests carry `Kind` (fixed after creation: a later request with another kind is
  400), `ShowInNavigation`, `NavigationOrder`. A page with a category, tags or a banner is 400
  (field named).
- **Reserved slugs:** `Croft.Shared.ReservedSlugs` lists every first path segment the Web app
  uses (the routes above, every `@page` route's first segment, `_framework`, `_content`, `css`,
  `js`, `fonts`, `favicon.ico`, ...). A page whose slug is reserved is 400; generated slugs that
  hit a reserved word get the usual numeric suffix. Guard test (Web unit): every `@page` route and
  every mapped endpoint's first literal segment is in `ReservedSlugs` (reflection over the
  components' `RouteAttribute`s plus the endpoint data source), so a future route can't silently
  shadow a page.
- Everything that lists posts returns posts only: `ListAsync` for the public lists, `RelatedAsync`,
  `SearchService`, the feed. The admin list shows both kinds with a Kind column and filter.
- `GET /api/pages/navigation` (anonymous): published pages with `ShowInNavigation`, ordered by
  `NavigationOrder` then title: `[{title, slug}]`. `GET /api/posts/{slug}` keeps working for
  pages for the editor; the public page fetch is `GET /api/pages/{slug}` (published only, 404
  otherwise; drafts for signed-in editors as posts do).
Tests (API): each 400 above; a published page is absent from every public list, search, related
and the feed, and present in `/api/pages/navigation` only when marked, in order; a draft page is
in none of them. Break-it-once: drop the kind filter from `ListAsync`; the home-list test fails.

## Item 2: writing a page

The admin sidebar gets "New page" under "New post" (`/admin/pages/new`, same editor component).
For a page the editor hides category, tags and banner and shows "Show in navigation" and
"Position" (a number); preview renders the page layout. The posts admin list's Kind filter
defaults to all. Publishing, scheduling, unpublishing and deleting work as for posts.
Tests (E2E): create a page with a slug, publish, it loads at `/<slug>`; mark it for navigation,
it appears in the sidebar for a signed-out reader; unmark, it goes; the editor shows no category,
tags or banner fields for a page and the posts list filter shows it under Pages.

## Item 3: the public page and the dates

- `Pages/StaticPage.razor`, `@page "/{slug}"`: below `NotFound`'s catch-all in precedence (a
  single segment route outranks `{**path}`), it fetches `/api/pages/{slug}` and renders the 404
  component with status 404 when there is none. Layout: the reading column (the post body's width
  and typography, `PostBody`), a plain title header (title, optional subtitle, the date line), no
  hero image, no rail, no Related, no reading time, no feedback.
- **Dates** (owner: "always worth having"): the date line shows the published date, and
  "· Updated <date>" when `UpdatedAt` is on a later calendar day (UTC) than `PublishedAt`. Same
  rule on posts' hero date line, which today shows only the published date. Confirm first that
  `UpdatedAt` changes only on content edits (not on view counts, publish/unpublish, scheduling);
  if it changes on anything else, report it and make the date line use a field that doesn't.
- SEO: canonical `/<slug>`, `og:type` `website`, JSON-LD `WebPage` with `datePublished` and
  `dateModified`; the sitemap lists published pages at `/<slug>` with `lastmod`.
- The sidebar: `MainLayout` loads `/api/pages/navigation` with the categories (one call each per
  circuit, as now) and passes it to `SidebarNav`, which renders the pages as `NavItem`s (icon
  `Description`) in the Home/Search section, after Search. Collapsed rail and phone drawer
  behave as for the other items.
Tests (E2E): a page renders title, date line and body at 1440×900 and 390×844, its body column
edges equal a post body's ±1px; an unknown `/<slug>` is 404 with the not-found page; a page
updated on a later day shows "Updated", one edited the same day doesn't (set the dates through
the API test hook or the database helper the E2E suite already uses for scheduling; if none
exists, test the rule in bUnit and say so); the sitemap contains the page; the feed doesn't.
Break-it-once: route pages before the fixed routes (or remove the reserved-slug check) so
`/search` is taken by a page called "search"; the reserved-slug test fails.

## Out of scope

Page hierarchies or sub-pages, per-page layouts, navigation outside the sidebar, menus with external links, migrating any post into a page.
