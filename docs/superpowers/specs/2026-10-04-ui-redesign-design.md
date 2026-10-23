# UI redesign: design spec

Date: 2026-10-04 · Status: awaiting review
Mockups: [`2026-10-04-ui-redesign/mockups/`](2026-10-04-ui-redesign/mockups/) (`.html`, open in a
browser). The site's brand assets (logo, tartan, icons), the mockup screenshots and the mockups that
embed them were removed before publication (site branding, image of unknown provenance); the table
below marks them.

## 1. Goal

Replace the default MudBlazor look ("fugly") with a personal, readable design for crawford.dev:
- sidebar navigation instead of the top bar;
- Material icons for categories;
- home-page cards that combine each post's banner, title, subtitle and first paragraph.

The admin area gets the same shell, plus the usability fixes needed to run the blog without typing
URLs or IDs.

**Success criteria**
- Every page matches the mockups' layout and style at desktop, tablet and phone widths, in light and dark.
- The sidebar and theme are correct on first paint, with no visible jump after load (proved by E2E tests with JavaScript disabled).
- An admin can list, create, edit, schedule, publish and unpublish posts, manage images, set
  category icons and resolve feedback, all from the UI.
- No serious or critical accessibility violations (axe) on home, post and editor pages.

## 2. Decisions (agreed during brainstorming)

| Topic | Decision | Mockup |
|---|---|---|
| Visual style | "Highland classic": warm off-white, serif headings, crimson accents | `visual-style` (option 1): mockup removed before publication (site branding, image of unknown provenance) |
| Home page | "Featured + list": newest post as a large overlay card, older posts as compact side-by-side cards | `post-cards` (option D) |
| Post page | Full-width banner hero, text column plus a right-hand column (TOC, tags, related) | `post-page-v2` (option 2): mockup removed before publication (site branding, image of unknown provenance) |
| No banner image | Darkened Crawford tartan with the category icon in white | `no-banner-v2` (option 2): mockup removed before publication (site branding, image of unknown provenance) |
| Logo | Rounded-square vector: white cog on darkened Crawford tartan | `logo` (option 2): mockup removed before publication (site branding, image of unknown provenance) |
| Second accent | Green for categories, tags and status only | `green-accent` (option B) |
| Sidebar | Full / icon rail / hidden by screen size; ☰ toggles | `layout-breakpoints`: mockup removed before publication (site branding, image of unknown provenance) |
| Theme | Follows the system by default, with a manual System → Light → Dark switch | n/a |
| Categories | Exactly one per post; tags for everything else | n/a |
| Category icons | Searchable picker over MudBlazor's Material icon set | n/a |
| Admin scope | Shell + theme, a posts list, editor upgrades, media library; no dashboards | n/a |
| Build approach | Own CSS layout shell around MudBlazor components, with one custom MudBlazor theme | n/a |

## 3. Visual identity

### 3.1 Colour tokens

Defined once as CSS custom properties on `:root` (light) and the dark selector (see 4.3), and fed into
the MudBlazor theme so both systems use the same values.

| Token | Light | Dark | Use |
|---|---|---|---|
| `--bg` | `#FAF7F4` | `#171314` | page background |
| `--surface` | `#FFFFFF` | `#221C1E` | cards, panels |
| `--text` | `#241A1C` | `#EDE6E7` | body text |
| `--muted` | `#5B4E51` | `#B9ACAF` | secondary text, meta |
| `--sidebar` | `#F1EAE6` | `#1E1819` | sidebar / top bar background |
| `--sidebar-text` | `#3A2A2E` | `#E3D9DB` | sidebar text |
| `--primary` | `#720534` | `#E88AA6` | links, focus rings, primary text accents |
| `--primary-strong` | `#720534` | `#7A2140` | filled backgrounds with white text (active nav, primary buttons) |
| `--primary-hover` | `#581E2F` | `#8E2A4E` | hover/pressed state of filled primary |
| `--primary-container` | `#F3E3E8` | `#3A2329` | subtle crimson tints |
| `--secondary` | `#074912` | `#7FB88F` | green lines and markers |
| `--secondary-container` | `#E3EFE6` | `#1F3226` | category chips, tag pills, status badges (background) |
| `--on-secondary-container` | `#074912` | `#A9D6B5` | text on the above |
| `--code-bg` / `--code-text` | `#2B2326` / `#F3E9EC` | same | code blocks |
| `--tartan-wash` | `rgba(25,8,14,.38)` | same | darkening layer over the tartan |

**Colour roles:** crimson means *act / you are here* (links, current page, buttons); green means
*classify / state* (category chips, tag pills, "Published" badges). Nothing else uses green.

**Status badges:** Published uses the green container; Scheduled uses an amber container
(`#FFF1D6` / `#7A4B00` light, `#3A2E14` / `#F2C877` dark); Draft is neutral grey.

### 3.2 Typography

Self-hosted WOFF2 files in `wwwroot/fonts` (no Google Fonts requests), loaded with `font-display: swap`:
- **Source Serif 4:** 400, 400 italic, 600, 700. Used for headings, post titles and subtitles, and article body.
- **Source Sans 3:** 400, 600. Used for UI text: sidebar, meta, chips, buttons, forms, admin tables.
- **JetBrains Mono:** 400. Used for code.

Article body: 18px / 1.65 on desktop, 17px on phones. Post hero title: 40px desktop, 30px phone.
Subtitles are Source Serif italic in `--muted`.

### 3.3 Crawford tartan and logo

(The asset files named here, `crawford-tartan.svg`, `logo.svg`, `favicon.ico` and `apple-touch-icon.png`, were removed from the repository before publication: site branding; the site's own repository keeps them.)

- `crawford-tartan.svg` is a 254×254 vector sett (crimson `#720534`, green `#074912`, white lines
  edged in green, with a twill effect). It's used as a repeating background, always under `--tartan-wash`.
- `logo.svg` is the rounded-square logo (white 8-tooth cog with a tartan centre, thin dark outline).
  Used as the SVG favicon and the sidebar/top-bar logo.
- `favicon.ico` (16/32/48) and `apple-touch-icon.png` (180) are generated from `logo.svg`.
  Reference all three plus a minimal web app manifest.
- Copy these files into `src/Blog.Web/wwwroot/`, replacing `favicon.png`.

## 4. Layout shell

### 4.1 Structure

A CSS grid shell (`MainLayout`) with a sidebar area and a content area. It does **not** use
`MudLayout`, `MudDrawer` or `MudAppBar`. MudBlazor components are used inside the content area and
the sidebar's controls.

| Width | Sidebar default | ☰ / Collapse behaviour |
|---|---|---|
| ≥ 1280px | full, 260px, in the layout | "Collapse" (sidebar footer) shrinks it to the rail; the choice is remembered per browser |
| 960–1279px | icon rail, 64px, in the layout; icons have tooltips | ☰ (top of rail) opens the full sidebar as an overlay over the content |
| < 960px | hidden; a 56px top bar with ☰, logo and blog name | ☰ slides the sidebar in as an overlay over the page with a scrim |

- The overlay closes on navigation, scrim click and Esc. While it's open, focus stays inside it and
  `aria-expanded` is kept in sync.
- A "Skip to content" link is the first focusable element.
- These states come from CSS media queries plus a class on `<html>`. A small JS module (served from
  `wwwroot`, not inline) handles ☰, Collapse, Esc and scrim, and saves `blog-sidebar=collapsed`.
  **With JavaScript disabled, every width must render its default state correctly.**

### 4.2 Sidebar contents (top to bottom)

1. Logo + blog name (`Blog:Name`) + tagline (`Blog:Tagline`), both from configuration.
2. Home, Search.
3. **Categories:** each category's icon, name and published-post count (categories with 0 posts hidden).
4. **Admin** (Admin role only): Posts, New post, Media, Tags & categories.
5. **Editor** (Editor role only): Drafts.
6. Footer: theme switch (System → Light → Dark, with a matching icon), Log in / Log out, Collapse (≥1280px only).

The current page is highlighted with `--primary-strong` and white text.

### 4.3 Theme switching without flash

- The default theme comes from a CSS `@media (prefers-color-scheme: dark)` block that sets the dark
  tokens. **No JavaScript is needed for the default.**
- An explicit choice is stored as `blog-theme=light|dark` in `localStorage`. A tiny inline script in
  `<head>` (the only inline script) sets `data-theme="light|dark"` on `<html>` before first paint. The
  token selectors are `:root[data-theme=dark]` and
  `@media (prefers-color-scheme: dark) { :root:not([data-theme=light]) }`.
- The same inline script applies `blog-sidebar=collapsed` as a class on `<html>`.
- `MudThemeProvider` gets `IsDarkMode` from the effective theme once the page becomes interactive.
  Accepted limitation: MudBlazor-styled controls may show their light colours for a moment on first
  load in dark mode. Layout surfaces, cards and text use our tokens and are correct immediately.

## 5. Public pages

### 5.1 Post card component

One `PostCard` component, used in two variants:
- **Featured:** the image area uses `aspect-ratio: 21 / 9` (max 420px tall; on phones `4 / 3`). It shows the banner (or tartan fallback with
  a large white category icon) under a bottom-to-top dark gradient. Overlaid on it: the green category
  chip (top-left), title and subtitle. Below the image: summary (3 lines max), date · reading time,
  "Read more →".
- **List:** image on the left at 34% width (min 96px on phones), and on the right the chip, title,
  subtitle, summary clamped to 2 lines, and date · reading time. On phones it stays side by side, with
  a smaller image.
- **No banner:** the tartan background at 180px tile size under `--tartan-wash`, with the category's
  icon in white (64px featured, 36px list). Posts with no category use the folder icon.
- The whole card is one link. The title text is the link's accessible name.

### 5.2 Home (`/`)

- Page 1: the newest published post as Featured, then the next 9 as List. Pages 2 and later: 10 List
  cards. Pagination via `?page=N`; `MudPagination` updates the URL. Empty state: "Nothing published yet."

### 5.3 Category (`/category/{slug}`), tag (`/tag/{slug}`), search (`/search?q=`)

- A header with the category icon + name + "· N posts", `#tag` + count, or "N results for 'q'". Then List
  cards, paged as on Home. No Featured card. Unknown category or tag slugs return the 404 page.

### 5.4 Post (`/post/{slug}`)

- **Hero:** full content width, 360px tall (240px on phones). Banner or tartan fallback, with a dark
  gradient. Overlaid on it: the category chip, title (h1), subtitle, and date · reading time.
- **Body:** text column `max-width: 68ch`, plus a 220px right-hand column with:
  - **On this page:** the post's h2/h3 headings, linking to their anchors (Markdig auto-identifiers),
    with the current section highlighted as you scroll (IntersectionObserver). Hidden when the post
    has fewer than 2 headings.
  - **Tags:** green pills linking to tag pages.
  - **Related:** up to 3 posts (thumbnail + title).
- **Responsive:** the right-hand column is shown when the **content area** is at least 1000px wide,
  using a CSS container query on the content area rather than a media query. Below that, "On this page"
  becomes a collapsible block above the text, and tags and related posts move to after the text.
- **Content styling:** images scale to the column (rounded, with a caption from the Markdown title if
  present); code blocks use `--code-bg` with syntax highlighting; tables get light borders.
- **Syntax highlighting:** highlight.js, self-hosted in `wwwroot/lib/highlightjs`, with languages C#,
  JavaScript/TypeScript, JSON, YAML, Bash, SQL, HTML/XML, CSS and Razor/cshtml (if no maintained
  Razor grammar exists, fall back to HTML). Use a custom theme stylesheet built from the colour
  tokens. Re-run after each interactive render.
- **Page views:** recorded as defined in brief 04 (browser beacon), unchanged by this design.

### 5.5 Not found

A themed page ("This page has wandered off…" plus links to Home and Search). Unknown routes and missing
posts render it with **HTTP status 404** (`NavigationManager.NotFound()` on .NET 10).

## 6. Admin and editor

### 6.1 Posts list (`/admin/posts`)

- `MudDataGrid`, paged on the server, with columns: title (link to the editor), status badge, date
  (published or scheduled; "–" for drafts), category icon, views, open-feedback count.
- Row actions: Edit, View (opens the public post or draft review), Analytics, Feedback (opens the editor
  with the Feedback tab), Delete (confirm dialog).
- Filters: status chips (All / Draft / Scheduled / Published) and a title search box.
- `/admin` redirects here. The old `/admin/post/{slug}`, `/admin/analytics` and `/admin/feedback`
  pages are removed.

### 6.2 Post editor (`/admin/posts/new`, `/admin/posts/{id}/edit`)

- **Left column (form):**
  - Title, Subtitle, "Search & link-preview description" (the `Excerpt` field).
  - Category (single select, showing icons).
  - Tags (chip input with autocomplete from existing tags; free text creates new tags).
  - Publish date + time in **UTC**, labelled so. Combined with `DateTime.SpecifyKind(..., Utc)`; never `ToUniversalTime()`.
  - **Banner panel:** thumbnail, Upload, Choose from library, Remove.
  - Markdown editor: a textarea with a toolbar for Bold, Italic, Heading, Link, Code, **Insert image**
    (upload, then insert `![alt](url)` at the cursor) and **Insert from library**.
- **Right column:** tabs.
  - **Preview:** renders with the real post styles, including the hero.
  - **Feedback:** open notes (quote + comment + author), each with **Resolve**. Resolved notes are
    collapsed under "Resolved (N)".
- **Actions bar:** Save draft · Publish now (publishes with the current time and ignores the date
  field) · Schedule (publishes with the entered date; enabled only when it's in the future) · Unpublish
  (only when Published or Scheduled). Every action saves the form first. It shows the status badge and "Saved hh:mm". Errors
  appear as an alert at the top, with the API's problem detail text.
- On phones the columns stack, with the form first.

### 6.3 Analytics (`/admin/posts/{id}/analytics`)

The post title, total views, and a `MudChart` line chart of daily views for the last 30 days.

### 6.4 Media library (`/admin/media`)

- A thumbnail grid of global images (toggle to "This post's images" when opened from the editor).
  Drag-and-drop or file-picker upload of several files at once, with a progress indicator.
- Each tile: the image, original name, and an **alt text** field (saves on blur), plus Copy Markdown
  (`![alt](url)`), Copy URL, and Delete. If the image is still a banner, Delete is refused with a
  message naming the post(s).
- The same grid opens as a dialog from the editor's "Insert from library" and "Choose from library",
  where clicking a tile selects it.

### 6.5 Tags & categories (`/admin/tags`)

- **Categories table:** icon (click to open the **icon picker** dialog), name (edit in place), post
  count, Delete. Delete is disabled while posts use the category, with a tooltip saying why.
- **Icon picker:** a search box and a virtualised grid of every `Icons.Material.Filled` icon, matched on
  name (e.g. "mount" → Terrain). Clicking an icon selects it; there's also a "Default (folder)" option.
- **Tags table:** name (edit in place), post count, Delete (only when unused).

### 6.6 Editor role

- `/editor`: drafts and scheduled posts (the same grid, read-only; actions: Review).
- `/editor/posts/{slug}`: the draft rendered with the **real post layout** and a "Draft" ribbon.
  Selecting text shows an "Add feedback" button. Notes appear in the right-hand column (or after the
  text on narrow screens).

## 7. Component structure

Blazor is component-based; build the UI from the components below. **Don't put this markup inline in
pages**, and don't duplicate it between pages. Names, folders and parameters are part of the design.
Small helpers may be added freely, but must not replace a listed component.

### 7.1 Rules

1. **Pages fetch, components present.** Routable pages (`@page`) call `IBlogApiClient` and pass DTOs
   down. Components receive data through `[Parameter]`s and report user actions through
   `EventCallback`s. Only the components marked **(service)** below may inject services.
2. **One component per file**, in the folder given. Each component's styles go in its own `.razor.css`
   (CSS isolation). Only the colour/typography tokens, fonts and the shell grid live in `wwwroot/app.css`.
3. **Reuse, don't copy.** The editor's Preview tab and the Editor role's draft review must render posts
   with the same `PostHero` + `PostBody` as the public post page. Every image that may lack a banner
   uses `BannerOrTartan`.
4. Each component listed here gets bUnit tests when it has logic (variants, conditional rendering,
   events). Pure markup wrappers don't need them.

### 7.2 Shell (`Components/Layout/`)

| Component | Parameters / behaviour | Used by |
|---|---|---|
| `MainLayout` **(service)** | Grid shell; loads categories once via `IBlogApiClient` and passes them to `SidebarNav`; hosts `SkipLink`, `TopBar`, `SidebarNav`, `@Body` | `Routes` |
| `SidebarNav` | `Categories`; renders `BrandBlock`, `NavItem`s, role sections (`AuthorizeView`), `ThemeSwitch`, Collapse | `MainLayout` |
| `BrandBlock` **(service)** | Logo + `BlogOptions` name/tagline; `Compact` (rail/top-bar) | `SidebarNav`, `TopBar` |
| `NavItem` | `Href`, `Icon` (Material icon name), `Text`, `Count?`, `Match` (prefix/all); active state from `NavLink` | `SidebarNav` |
| `TopBar` | Phone only: ☰ button (`aria-controls`/`aria-expanded`) + `BrandBlock Compact` | `MainLayout` |
| `ThemeSwitch` **(service)** | Cycles System → Light → Dark via the sidebar JS module; raises `ThemeChanged` | `SidebarNav` |
| `SkipLink` | "Skip to content" | `MainLayout` |

Replaces `NavMenu.razor`.

### 7.3 Blog (`Components/Blog/`)

| Component | Parameters | Used by |
|---|---|---|
| `PostCard` | `Post` (`PostSummaryDto`), `Variant` (`Featured` / `List`) | `PostList` |
| `PostList` | `Items`, `ShowFeatured` (bool), `Page`, `TotalPages` | Home, Category, Tag, Search pages |
| `BannerOrTartan` | `Banner?`, `Category?`, `IconSize`, `Overlay` (gradient on/off); renders the image or the tartan + icon fallback | `PostCard`, `PostHero`, `RelatedPosts`, `MediaGrid` thumbnails where relevant |
| `CategoryChip` | `Category?`; green chip with icon, links to the category page | `PostCard`, `PostHero` |
| `TagPills` | `Tags` | `PostRail` |
| `PostMeta` | `PublishedAt`, `ReadingMinutes`, `Status?` (shows a status badge in admin/draft contexts) | `PostCard`, `PostHero` |
| `PostHero` | `Post` (`PostDto`), `DraftRibbon` (bool) | Post page, editor Preview, draft review |
| `PostBody` | `Markdown`; renders via `IMarkdownRenderer` **(service)** and triggers highlight.js after render | Post page, editor Preview, draft review |
| `PostRail` | `Headings`, `Tags`, `Related`, `ChildContent` (extra panels, e.g. feedback) | Post page, draft review |
| `TableOfContents` | `Headings` (text + anchor + level); scroll-spy via JS module | `PostRail` (and the collapsed block) |
| `RelatedPosts` | `Items` (`PostSummaryDto`) | `PostRail` |
| `PagePager` | `Page`, `TotalPages`; builds `?page=N` links | `PostList` |
| `NotFoundContent` | none | Not-found page, unknown slugs |

The heading list for `TableOfContents` comes from `IMarkdownRenderer` (extend it to return the HTML plus
the h2/h3 headings with their generated ids), so headings are parsed once.

### 7.4 Admin and editor (`Components/Admin/`)

| Component | Parameters / behaviour | Used by |
|---|---|---|
| `StatusBadge` | `Status` | `PostsGrid`, `PostMeta`, editor actions bar |
| `PostsGrid` | `ServerData` callback (paging/filter), row action `EventCallback`s; `ReadOnly` for the Editor role | `/admin/posts`, `/editor` |
| `PostEditorForm` | `Model` (edit model), `Categories`, `AllTags`; `OnSave`, `OnPublishNow`, `OnSchedule`, `OnUnpublish` | `/admin/posts/new`, `/admin/posts/{id}/edit` |
| `MarkdownEditor` | `@bind-Value`; toolbar; `OnInsertImageRequested`, `OnInsertFromLibraryRequested`; inserts text at the cursor via JS | `PostEditorForm` |
| `BannerPicker` | `Banner?`, `OnUpload`, `OnChooseFromLibrary`, `OnRemove` | `PostEditorForm` |
| `MediaGrid` **(service)** | `Mode` (`Manage` / `Pick`), `PostId?`; lists, uploads, edits alt text, deletes; `OnPicked` in Pick mode | `/admin/media`, `MediaPickerDialog` |
| `MediaPickerDialog` | `MudDialog` wrapping `MediaGrid Mode=Pick` | editor (banner and inline image) |
| `IconPicker` **(service)** | `@bind-Value` (icon name); search over `IIconCatalog`; virtualised grid | `IconPickerDialog` |
| `IconPickerDialog` | `MudDialog` wrapping `IconPicker` | `/admin/tags` |
| `CategoryIcon` **(service)** | `Name?`, `Size`; resolves via `IIconCatalog` with the Folder fallback | `NavItem`, `CategoryChip`, `BannerOrTartan`, grids |
| `FeedbackPanel` | `Items`, `CanResolve`, `OnResolve`, `CanAdd`, `OnAdd` (quote + comment) | editor Feedback tab, draft review (in `PostRail`) |
| `ViewsChart` | `Daily` (date + count) | `/admin/posts/{id}/analytics` |

### 7.5 Pages (`Components/Pages/`)

Pages stay thin: route parameters, loading/empty/error states, one `IBlogApiClient` call per piece of
data, and composition of the components above. Admin pages live in `Pages/Admin/`
(`PostsPage`, `PostEditPage`, `PostAnalyticsPage`, `MediaPage`, `TagsPage`); editor pages in
`Pages/Editor/` (`DraftsPage`, `DraftReviewPage`). Delete the old pages these replace
(`PostEditor`, `Analytics`, `FeedbackReview`, `MediaLibrary`, `TagsAndCategories`, `Drafts`,
`DraftReader`).

## 8. Data model and API

### 8.1 Model changes (one EF migration)

- `Post`:
  - Add `Subtitle` (string?, max 300).
  - Add `CategoryId` (Guid?, FK, `OnDelete: SetNull`). The migration copies each post's first category
    from the existing join table, then **drops** the many-to-many `Categories` relation.
  - Add `Summary` (string?, max 320) and `ReadingMinutes` (int), recalculated on every create and update.
- `Category`: add `Icon` (string?, max 64, must match `^[A-Za-z0-9]+$` if set).
- `MediaAsset`: add `AltText` (string?, max 300).

**Backfill:** an idempotent startup task in the API fills `Summary` and `ReadingMinutes` for posts
where `Summary` is null.

**Summary rules:** the first top-level paragraph that isn't only an image. Plain text with markup
removed and whitespace collapsed; cut at a word boundary to at most 300 characters, with "…" added when
cut. Empty if no such paragraph exists.

**Reading time:** `ceil(words / 220)`, minimum 1. Words are counted from the plain text, excluding
fenced code blocks.

### 8.2 Contracts (`Blog.Shared`)

- New `PostSummaryDto`:

  | Field | Notes |
  |---|---|
  | `Id`, `Slug`, `Title`, `Subtitle`, `Summary`, `ReadingMinutes`, `Status`, `PublishedAt` | |
  | `Category` | `CategoryDto?` |
  | `Tags` | |
  | `Banner` | `{ Url, AltText }?` |
  | `ViewCount`, `OpenFeedbackCount` | `long?` / `int?`; **null unless the caller is Admin** |

- `PostDto` adds `Subtitle`, `Category` (single `CategoryDto?`, replacing the list) and `ReadingMinutes`.
- `CategoryDto` adds `Icon` and `PostCount` (published posts).
- `CreatePostRequest` / `UpdatePostRequest`: add `Subtitle`; replace `Categories` (names) with
  `CategoryId` (Guid?). `Tags` stay as names.
- `MediaAssetDto` adds `AltText`.

### 8.3 Endpoints

| Method | Path | Role | Notes |
|---|---|---|---|
| GET | `/api/posts` | public / Admin, Editor | **returns `PagedResult<PostSummaryDto>`**; query: `page`, `pageSize`, `status` (Admin/Editor only), `q` (title search), `category`, `tag` |
| GET | `/api/posts/{slug}/related?count=3` | public | published only. Score = 2 × shared tags + 1 if same category; order by score desc, then newest. If fewer than `count` posts score above 0, fill with the newest published |
| POST | `/api/posts/{id}/unpublish` | Admin | Published/Scheduled → Draft; `PublishedAt` cleared |
| POST | `/api/posts/{id}/feedback/{feedbackId}/resolve` | Admin | sets `ResolvedAt`; 404 if the feedback isn't on that post |
| PUT | `/api/media/{id}` | Admin | `{ altText }` |
| DELETE | `/api/media/{id}` | Admin | 409 with the post titles if it's any post's banner; deletes the file too |
| PUT / DELETE | `/api/categories/{id}` | Admin | `{ name, icon }`; DELETE → 409 if any post uses it |
| PUT / DELETE | `/api/tags/{id}` | Admin | rename; DELETE → 409 if any post uses it |
| GET | `/api/categories` | public | includes `Icon` and `PostCount` |

All new errors use the existing domain exceptions and ProblemDetails mapping.

### 8.4 Web services

- `IBlogApiClient` is updated for the endpoints above.
- `IIconCatalog` (singleton): reads `MudBlazor.Icons.Material.Filled`'s public constants by reflection
  once, giving name → SVG path, plus search. Unknown or empty names give `Folder`.
- `BlogOptions` (`Blog:Name`, `Blog:Tagline`) bound from configuration.

## 9. Testing

Follows AGENTS.md (TDD, outcome-based assertions, a break-it-once check for key tests).

- **API integration tests:**
  - migration and backfill (first category kept; Summary/ReadingMinutes filled);
  - Summary extraction cases (headings, image-only paragraph, code, formatting, truncation);
  - related ranking; unpublish; resolve (including the wrong-post 404);
  - media alt text, delete 409 for banners, and file removal;
  - category and tag delete 409;
  - the list response contains no Markdown, and the admin-only counts are null for anonymous callers.
- **bUnit (new in `Blog.Web.Tests`)**, for components with logic (section 7.1 rule 4), at least:
  - `PostCard` featured/list × banner/fallback;
  - the icon picker filters;
  - the sidebar shows Admin only for the Admin role and Editor only for Editor.
- **Playwright E2E** (existing selectors updated deliberately):
  1. With **JavaScript disabled**, at 1440 / 1100 / 390px widths: full sidebar / rail / top bar shown, as specified.
  2. With JS on at 390px: ☰ opens the overlay; clicking a category navigates and closes it; Esc closes it.
  3. With dark colour-scheme emulation and **JS disabled**: the body background is the dark `--bg`.
     Then switch the theme to Light, reload, and the light background persists.
  4. Home: the featured and list cards show subtitle and summary. A post without a banner shows the
     tartan fallback with its category icon.
  5. Post: the right-hand column is visible at 1440px; at 1100px with the sidebar open it's collapsed
     into the "On this page" block. A TOC link scrolls to its heading. Related posts appear.
  6. Admin: Posts list → Edit → change subtitle → Save → the public post shows it; Unpublish removes
     it from Home; Insert from library adds `![alt](url)`; deleting a banner image is refused; choosing a
     category icon shows it in the sidebar.
  7. Editor: add feedback on a draft → the admin resolves it in the editor's Feedback tab → it's no
     longer open.
  8. `/post/does-not-exist` returns status 404 and the themed page.
- **Accessibility:** Deque.AxeCore.Playwright on Home, Post and the Editor in light and dark. Fail on
  serious or critical violations.
- **Screenshots** (review only, not asserted): Home, Post, Posts list, Editor at 1440 / 1100 / 390px,
  light and dark, saved to the test output.
- **Break-it-once checks:** remove the sidebar media query → test 1 fails; remove the tartan fallback
  → test 4 fails; return 200 from the not-found page → test 8 fails.

## 10. Ordering and dependencies

- Implement **after** briefs 01 (small fixes), 02 (token refresh) and 04 (analytics), which are already
  planned and touch `Post.razor` and the Web token flow.
- Brief 05 (editors creating drafts), if wanted, should come **after** this and use `CategoryId`.
- Brief 03 (Kubernetes) must add the self-hosted fonts and the new static assets to the image (no
  action expected beyond a normal publish) and the `Blog:Name` / `Blog:Tagline` settings.

## 11. Out of scope

Comments, newsletters/RSS changes, multi-language, dashboards or charts beyond per-post views, bulk
actions, multiple categories per post, a WYSIWYG editor, image editing/cropping, and image resizing or
thumbnail generation (images are served as uploaded; the CSS crops with `object-fit`).
