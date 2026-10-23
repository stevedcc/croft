# Brief 10f: a site footer, and pages placed in the sidebar or the footer

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item,
TDD, break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:**
- Every page has a footer with the owner's legal pages (Impressum, Privacy) and "Running on
  Croft vX.Y.Z".
- In the page editor, the owner chooses where a page appears: nowhere, in the sidebar or in the
  footer, ordered by a number.
- A new admin "Pages" list shows every page with its placement and order. "Posts" lists only
  posts.

**Evidence:**
- German law expects an Impressum reachable from every page. crawford.dev's new
  `/privacy-policy` page is reachable only because the owner put it in the sidebar as a stopgap.
- Pages already have `ShowInNavigation` (bool) and `NavigationOrder` (int) (`Post.cs` lines
  22–25). The sidebar lists published pages with `ShowInNavigation`, ordered by
  `NavigationOrder`, then title (`PostService` ~line 132).
- There is no list of pages: they appear mixed in with posts under Admin → Posts.

## Item 1: a page's placement is None, Sidebar or Footer

**Do:**
- Replace `ShowInNavigation` with `NavigationPlacement`, an enum `None | Sidebar | Footer`
  stored as a string, across:
  - the model;
  - the EF configuration, with a migration that maps `true → Sidebar` and `false → None`, then
    drops the old column;
  - `PostDto`, `CreatePostRequest` and `UpdatePostRequest`;
  - `PostEditModel`;
  - the editor.
- Contract compatibility: the API and Web deploy together, so no compatibility shim. Keep the
  DTOs' defaults (`None`).
- **Editor (pages only):** a three-option control labelled "Show in", with "Nowhere", "Sidebar"
  and "Footer", replacing the checkbox. The order field is labelled "Order (lowest first)".
  - A plain `<select>` or radio group matching the form's existing plain controls is fine. No new
    MudBlazor components.
- **API:**
  - The existing navigation endpoint returns the sidebar's pages: `Placement == Sidebar`,
    published, `Kind == Page`, ordered by `NavigationOrder` then title.
  - Add an equivalent footer list: `Placement == Footer`, same rules. One endpoint with a
    placement parameter, or two; rule it.
  - Both lists go through the public read cache, with invalidation on page save and publish,
    like the sidebar list today.
- **The full draft backup from brief 10a must include the new field.** 10a's
  property-enumeration test enforces this; keep it green.

**Tests:**
- Migration test: rows with `true`/`false` become `Sidebar`/`None`.
- API: the sidebar list excludes Footer and None pages and drafts; the footer list excludes
  Sidebar and None pages and drafts; both use the order then the title.
- bUnit: the editor shows the placement control for pages only, and saves the chosen value.
- Break-it-once: map `true` to `None` in the migration; the migration test must fail.

## Item 2: the site footer

**Design:**
- **Where:** at the bottom of the content column on every document, static and interactive,
  admin included. It isn't fixed or sticky; it comes after the page content.
- **Content, in one quiet line or two on a phone:**
  - the footer pages' titles as links, in order, separated as the design system separates inline
    links;
  - then "Running on Croft vX.Y.Z", where "Croft" links to the project's repository URL from a
    new branding setting `PoweredByUrl`. With no URL, it's plain text.
- **Version:** the real released version, stamped at build.
  - Check how `0.6.0` is recorded today (the `v0.6.0` git tag; there is no `<Version>` in
    `Directory.Build.props`).
  - Add a single source: `<Version>` in `Directory.Build.props`. The release bumps it; set it to
    `0.6.1` in this brief.
  - Read it from `AssemblyInformationalVersion` without the `+commit` suffix.
- **Configuration** in `branding.json`:
  - `ShowPoweredBy`, default true;
  - `PoweredByUrl`, default empty. Document it next to the other branding settings.
- **No footer pages and `ShowPoweredBy: false`:** no footer element is rendered at all.
- **Style:** UI font, small (the sidebar's label size), muted text colour, links in the link
  colour, a top border or spacing consistent with the content surfaces. Light and dark contrast
  of at least 4.5:1. Use the alpha-aware `ColorContrast` helper from brief 10b.
- **Static and CSP-clean:** a plain component with no MudBlazor and no script. The footer pages
  come from the same cached navigation call path the sidebar uses, so a reading page makes no
  extra API call when the cache is warm. Check `ReadingPageApiCountTests` and extend it.
- **Accessibility:** `<footer>` with `role="contentinfo"`, only once per document. Links are
  reachable by keyboard and show the focus ring.

**Tests:**
- E2E: at 1440 and 390 px, on Home, a Post, a static page and an admin page:
  - the footer shows the footer pages in order, then "Running on Croft v0.6.1";
  - a Sidebar page is **not** in the footer, and a Footer page is **not** in the sidebar.
- E2E: the contrast in light and dark.
- bUnit: no footer element when there are no footer pages and `ShowPoweredBy` is false.
- `ReadingPageApiCountTests`: no extra API call per reading page with a warm cache.
- Unit: the version string has no `+commit` suffix and equals `<Version>`.
- Break-it-once: drop the footer from `MainLayout`; the E2E must fail.

## Item 3: an admin "Pages" list; "Posts" lists posts only

**Do:**
- A new sidebar entry "Pages" under Admin, next to "Posts". `/admin/pages` reuses the posts
  grid component filtered to `Kind == Page`, with columns:
  - title;
  - status;
  - **Show in** (–, Sidebar, Footer);
  - **Order**;
  - updated.
  Sort by placement (Sidebar, Footer, None), then order, then title. Row actions: edit and view.
  Pages have no analytics, so leave that action out.
- `/admin/posts` filters to `Kind == Post`.
- **Check the redirect after "New page" saves:** the editor's return path goes to `/admin/pages`
  for pages and `/admin/posts` for posts.

**Tests:**
- E2E:
  - `/admin/pages` lists a Footer page and a Sidebar page with their placement and order, and
    no posts;
  - `/admin/posts` lists no pages;
  - creating a page from "New page" returns to `/admin/pages` after the first save, if the
    editor navigates at all.
- Break-it-once: remove the Kind filter from `/admin/posts`; its test must fail.

## Out of scope

- Writing the Impressum or Privacy content. The owner does that as pages.
- A page appearing in both the sidebar and the footer (owner decision: one placement per page).
- Menus with nested items, or links to external URLs in the footer.
