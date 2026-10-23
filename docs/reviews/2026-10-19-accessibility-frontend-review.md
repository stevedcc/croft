# Accessibility and front-end robustness review — 2026-10-19

Scope: the seven static reading pages, the shell (`MainLayout`, `TopBar`, `SidebarNav`, footer),
the admin and editor UI, and `static-glue.js`, `post.js`, `shell.js`. Standard: WCAG 2.2 AA
(2.3.3 Animation from Interactions is AAA; it is included because the brief asked for reduced
motion). Branch `review/accessibility-frontend-review`; this file is the only change.

Earlier reviews are cross-referenced, not repeated: PR #3 (public-release readiness), PR #4
(.NET code review), PR #5 (test suite review).

Contrast ratios below were computed with the WCAG 2.x relative-luminance formula from the
palette values in `src/Croft.Web/Branding/BrandPalette.cs` (defaults) and
`branding.example/branding.json`, and from the literal colours in the CSS. Numbers are
rounded to two decimals.

## Verification

| Check | Result |
|---|---|
| `dotnet build Croft.slnx` (SDK 10.0.4xx installed into `~/dotnet`) | 0 warnings, 0 errors |
| `dotnet test tests/Croft.Web.Tests --no-build` | Failed 31, Passed 1057, Total 1088. All 31 failures are `DockerUnavailableException` from Testcontainers (no container runtime in this environment; PR #3 I-5 covers it). No front-end test failed. |
| `dotnet test tests/Croft.Api.Tests` | **unverified** (needs a Postgres container) |
| `dev/run-e2e.sh`, including `AccessibilityTests` and `KeyboardFocusTests` | **unverified** (needs Podman for the E2E instances; Chromium alone is not enough). The E2E findings below come from reading the tests. |
| Whether axe-core 4.13 enables the `target-size` rule by default | **unverified** (could not locate `axe.min.js` in the package cache) |

## Findings

No Critical findings.

### Important

#### I-1. Keyboard focus on the file inputs is invisible (2.4.7 Focus Visible)

`src/Croft.Web/Components/Admin/BannerPicker.razor.css:76-81` and
`src/Croft.Web/Components/Admin/MarkdownEditor.razor.css:47-52`:

```css
    position: absolute;
    inset: 0;
    opacity: 0;
```

Both file inputs (`aria-label="Upload banner"`, `aria-label="Upload inline image"`) are
stretched over their visible button and made fully transparent. Neither file has a
`:focus-visible` or `:focus-within` rule for them (the `:focus-visible` rules at
`BannerPicker.razor.css:64` and `MarkdownEditor.razor.css:35` are for the sibling buttons), and
the global rule at `src/Croft.Web/wwwroot/app.css:126` does not cover form fields:

```css
:where(a[href], button, summary, [tabindex]:not([tabindex="-1"])):not([class*="mud-"]):focus-visible {
```

Scenario: an editor tabs through the toolbar. After the last formatting button, focus lands on
the hidden input: nothing on screen changes, and pressing Space opens a file dialog from
"nowhere". Tab again and the focus ring reappears on the textarea. The same happens on the
banner picker.

Fix: add `:focus-within` rules on the wrappers of `.banner-picker__file` and `.md-editor__file` that
draw the same outline the sibling buttons use (compare `MediaGrid.razor.css:33`, which does
exactly this), or add `input, select, textarea` to the global selector in `app.css:126`.

#### I-2. Category chip on post cards is under 24 px and overlaps the card link (2.5.8 Target Size)

`src/Croft.Web/Components/Blog/CategoryChip.razor.css:8-13`:

```css
    padding: 2px 10px;
    border-radius: 10px;
    …
    font-size: 12px;
```

No border, no `min-height`: 12 px text with `line-height: normal` gives roughly 16–18 px, plus
4 px padding, so the chip is about 20–22 px tall. It sits as a sibling of the card-wide
`<a class="post-card__link">` (`PostCard.razor:8`), so the only thing touching its edge is
another link. 2.5.8 allows an undersized target only when a 24 px circle centred on it meets
no other target; here the card link surrounds it on every side.

Scenario: on a phone a reader aims at a chip to open the category and lands on the post
instead. `TagPills.razor.css` has the same 12 px text but a 1 px border, which brings it to
24 px exactly; the chip has none.

Fix: `min-height: 24px` (or `padding: 4px 10px` plus a border) on `.category-chip`.

#### I-3. Search field: hairline below 3:1, and the ARIA on it is wrong (1.4.11, 4.1.2)

`src/Croft.Web/Components/Pages/Search.razor.css:12`:

```css
    --field-line: rgb(189 189 189);
```

Against the default light background (`#FFFFFF`) that is 1.80:1; the field's only visible
boundary is this line, so it fails 1.4.11 Non-text Contrast (3:1) in light mode. The dark
value (`rgb(255 255 255 / .298)`, line 18/25) resolves to roughly 3.1:1 over the dark
background and passes.

`src/Croft.Web/Components/Pages/Search.razor:26-38`:

```html
type="search" role="textbox" placeholder=" " autocomplete="off" inputmode="text"
…
<button class="search-field__button" aria-label="Search" tabindex="-1" type="submit">
    <svg class="search-field__icon" focusable="false" viewBox="0 0 24 24" aria-hidden="true" role="img">
…
<fieldset class="search-field__border"><legend>Search</legend></fieldset>
```

- `role="textbox"` on `type="search"` demotes the native `searchbox` role, so screen readers no
  longer announce it as a search field. `aria-invalid="false"`, `aria-required="false"` and
  `maxlength="524288"` are MudBlazor-markup imitations with no function on a static page.
- The submit button has `tabindex="-1"`: keyboard users cannot reach it. Enter in the field
  still submits, but a user who tabs past the field to "press Search" finds nothing.
- `aria-hidden="true"` together with `role="img"` is contradictory; the icon is already hidden.
- The decorative `<fieldset><legend>Search</legend></fieldset>` is a second, empty group named
  "Search" in the accessibility tree.

Fix: `--field-line` at least `rgb(118 118 118)` (3.0:1 on white); drop `role="textbox"`,
`aria-invalid`, `aria-required`, `maxlength`; remove `tabindex="-1"` from the button; drop
`role="img"`; give the fieldset `aria-hidden="true"` or replace it with a border on the box.

#### I-4. Save-state and status changes in the editor are not announced (4.1.3 Status Messages)

`src/Croft.Web/Components/Admin/PostEditorForm.razor:14`:

```razor
<span class="post-actions__saved">Saved @SavedAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture)</span>
```

The text that tells an author the draft is saved has no live region, and nothing else in the
form (`Publish`, `Unpublish`, `Schedule`) announces its result either. A screen-reader user
presses "Save draft" and hears nothing; the only way to confirm is to move focus around and
find the span.

Fix: `role="status"` (or `aria-live="polite"`) on a container that is always rendered, with
the "Saved HH:mm" text and the publish/unpublish outcomes written into it.

#### I-5. Rename on the Categories/Tags page drops keyboard focus (2.4.3 Focus Order)

`src/Croft.Web/Components/Pages/Admin/TagsPage.razor:61-69` (categories) and `124-132`
(tags):

```razor
<MudTextField @bind-Value="row.Name" Label="Name" Variant="Variant.Outlined"
              Margin="Margin.Dense" OnBlur="() => SaveCategoryNameAsync(row)" />
…
<MudButton Size="Size.Small" aria-label="@($"Rename {row.Name}")"
           OnClick="() => StartRenameCategoryAsync(row)">Rename</MudButton>
```

Activating Rename removes the button the user is focused on and renders a text field in its
place; nothing moves focus into the field, so focus falls back to `<body>`. The rename is
committed on blur only, so a keyboard user has to tab back into the field, type, and tab out.
There is also no Escape or Cancel.

The disabled Delete button at `73-78` sits inside a `MudTooltip` that explains why it is
disabled; a disabled button is not focusable, so the keyboard user never gets the explanation.

Fix: after `StartRename…`, focus the field (`MudTextField.FocusAsync()` in
`OnAfterRenderAsync`), commit on Enter and cancel on Escape; put the reason for the disabled
Delete in visible text (or `aria-describedby`) rather than a hover tooltip.

#### I-6. Posts grid: category is conveyed by an icon with no name (1.1.1, 1.3.1)

`src/Croft.Web/Components/Admin/PostsGrid.razor:66-70`:

```razor
<TemplateColumn T="PostSummaryDto" Title="Category">
    …
    <span class="posts-grid__category"><CategoryIcon Name="@context.Item.Category?.Icon" Size="20" /></span>
```

and `src/Croft.Web/Components/Admin/CategoryIcon.razor:5`: `<svg … aria-hidden="true">`. The
cell contains nothing but a hidden SVG, so a screen reader reads an empty Category cell.
Sighted users must also know the icon set to tell categories apart.

The row actions at `92-101` (`Edit`, `Analytics`, `Feedback`, `Delete`) are repeated per row
with no row context; a screen reader listing buttons hears "Delete, Delete, Delete…". Grid
cells give context when navigated as a table, but not from the buttons list or on a 390 px
layout where MudBlazor's data grid may linearise.

Fix: render the category name (visually hidden if the design wants icon-only) next to the
icon; add `aria-label="Delete @context.Item.Title"` (and so on) to the row buttons.

#### I-7. Contents-panel toggle has no accessible name until JS runs, then is named by its content

`src/Croft.Web/Components/Blog/ContentsPanel.razor:19-20`:

```razor
<button type="button" class="toc-inline__row" aria-expanded="false" aria-controls="toc-inline-body">
    <span class="toc-inline__row-text"></span>
```

Server-rendered, the button is empty: no text, no `aria-label`. `post.js:556 syncPanelRow()`
later writes the current section's title into the span, so the button is announced as, say,
"Installing, collapsed" rather than "Contents" or "On this page". Before `post.js` loads (or
when it fails, see I-10) it is an unnamed button, which axe reports as `button-name`
(serious).

Fix: a constant `aria-label="Contents"` on the button, keeping the section title as visible
text, or render the title text server-side.

#### I-8. Palette contrast warnings check 7 pairs; the CSS uses more (1.4.3 for site palettes)

`src/Croft.Web/Branding/PaletteTokens.cs:39-48`:

```csharp
    public static readonly IReadOnlyList<(string Foreground, string Background)> ContrastPairs =
    [
        (Text, Bg),
        (Text, Surface),
        (Muted, Bg),
        (Primary, Surface),
        (OnPrimary, Primary),
        (OnSecondaryContainer, SecondaryContainer),
        (SidebarText, Sidebar),
    ];
```

`BrandingService.WarnBelowAA` (`BrandingService.cs:411`) warns, at Warning level only, on these
pairs. Pairs the stylesheets actually render are missing: `muted` on `surface` (post-card meta),
`primary` on `bg` (links in body copy), `on-primary` on `primary-strong` (hover state of the
primary button), `on-secondary-container` on `bg` (tag pills on the post page), `primary` on
`primary-container` (active sidebar item). A site owner can ship a palette that passes every
check and still has unreadable post-card dates. The default and example palettes pass all of
these (see "Checked and found sound"), so this is a guard gap, not a current failure.

Fix: extend `ContrastPairs` to every foreground/background pair the CSS combines, and make the
2.5.8-style non-text pairs (`line`/`bg` at 3:1) part of the same check.

#### I-9. Hero subtitle can fall to 3.94:1 over a light banner (1.4.3)

`src/Croft.Web/Components/Blog/PostHero.razor.css:56,75`:

```css
    background: linear-gradient(to bottom, rgba(0, 0, 0, 0) 0, rgba(0, 0, 0, .55) 4rem, rgba(0, 0, 0, .75) 100%);
…
    color: rgba(255, 255, 255, .85);
```

The file's own comment (lines 43-44) works out that white text over a white banner at the
top of the text block, through 55 % black, has a backdrop of sRGB 115 per channel and just
clears 4.5:1 (4.74). The subtitle is not white: at 85 % opacity over that backdrop it
composites to about sRGB 234, which gives 3.94:1. On a dark banner the ratio is far higher;
the failure needs a pale banner and a subtitle, both of which the example branding provides.

Fix: full-opacity white for the subtitle, or raise the gradient's mid-stop to about .62.

#### I-10. `boot()` has no error handling; one failed import leaves the page half-initialised

`src/Croft.Web/wwwroot/js/static-glue.js:23-29,45,64`:

```js
    window.Blazor?.addEventListener("enhancedload", boot);
…
        await import("./post.js");
…
    root.dataset.interactive = "on";
```

`boot` is an async function used directly as an event listener. If `import("./post.js")`
rejects (a CSP block, a stale cached module after a deploy, a network error on a slow
connection) or any later call throws, the rejection is unobserved: the theme switch label
(W-086), the page-view beacon and `data-interactive="on"` are already done or never set, the
contents panel and scroll spy are not started, and the previous post's listeners (W-085) are
not torn down, so the old page's `scroll` handler keeps running against detached nodes until
the next successful boot. The E2E suite waits on `data-interactive`, so a flaky import shows
up as a timeout with no cause in the logs (PR #5 I-4 touches the same signal).

Fix: wrap the post-page branch in `try … catch (e) { console.error(…) }`, run
`window.blogPost?.teardownPost()` before the import rather than after, and set
`data-interactive` in a `finally` so the shell is usable even when the post extras fail.

#### I-11. E2E accessibility coverage has gaps the brief would expect to be closed

`tests/Croft.Web.E2E/AccessibilityTests.cs`:

- Every axe scan runs at `ViewportSize = new() { Width = 1440, Height = 900 }` (lines 82, 100,
  120, 137, …). Nothing scans the drawer, the top bar, or the inline contents panel, which only
  exist below 960/1280 px. `NoHorizontalOverflow_At390` checks overflow only.
- Pages scanned: Home, Post, admin Posts, Post editor, editor list, editor review. Not scanned:
  Search (I-3), Categories, Category, Tag, Static page, 404, Media, Tags/Categories admin
  (I-5), Pages admin, Analytics, and every MudDialog.
- Line 417-420: moderate and minor violations are logged with `TestContext.Progress` and never
  fail. 1.4.11 hairline contrast, `region`, `landmark-*` and `heading-order` are moderate in
  axe, so I-3 and the Minor landmark/heading findings below would pass the suite today.
- The target-size rule (2.5.8) is not asserted anywhere; `KeyboardFocusTests` checks outline
  width and contrast but only on Home, Post and 404.

Fix: a `TestCase` matrix over all public routes at 1440 and 390 in both themes; fail on
moderate too (with an explicit allow-list for known items); add `target-size` and
`color-contrast-enhanced` to the rules if the axe build does not enable them.

### Minor

#### M-1. `contentinfo` landmark nested inside `main`

`src/Croft.Web/Components/Layout/MainLayout.razor` renders `<SiteFooter/>` inside
`<main id="content" tabindex="-1">`, and `SiteFooter.razor:12` is
`<footer class="site-footer" role="contentinfo">`. A `footer` inside `main` is not a landmark
by HTML semantics, but the explicit `role="contentinfo"` makes it one, nested in `main`; axe
reports `landmark-contentinfo-is-top-level` (moderate), and landmark navigation shows
"content info" as a child of "main". Fix: move the footer out of `<main>` or drop the role.

#### M-2. Drawer, reconnect modal and search transitions ignore `prefers-reduced-motion`

`src/Croft.Web/wwwroot/app.css:233,241,274` (`transition: transform .2s` on the sidebar
drawer), `Components/Layout/ReconnectModal.razor.css:7-17` (an infinite
`components-rejoining-animation`), `Components/Pages/Search.razor.css:109,133,154,208`. The only
reduced-motion rule in the Web app is `ContentsPanel.razor.css:255`. Fix: one global
`@media (prefers-reduced-motion: reduce) { *, ::before, ::after { transition-duration: 0.01ms !important; animation-iteration-count: 1 !important; } }`
in `app.css`.

#### M-3. Sidebar `h2`s precede the page `h1` in document order

`src/Croft.Web/Components/Layout/SidebarNav.razor:26,41,55`:
`<h2 class="label section-heading">Categories</h2>` (and Admin, Editor) are rendered before
`<main>`, so heading navigation starts at h2 and reaches h1 later; axe `heading-order` is
moderate. Fix: `aria-labelledby` on the nav groups with the heading demoted to a `div`, or
`role="presentation"`-free plain `<span>` labels.

#### M-4. Category post counts are read without context

`src/Croft.Web/Components/Layout/NavItem.razor:14`: `<span class="label count">@Count</span>`.
The link reads as "Tutorials 12"; nothing says "12 posts". Fix: visually hidden " posts" after
the number, or `aria-label="Tutorials, 12 posts"` on the link.

#### M-5. Page meta rendered inside the `h1`

`src/Croft.Web/Components/Blog/PageHeader.razor:36-40` renders `<span class="page-header__meta">`
inside the `h1` block, so the heading reads "Tutorials · 12 posts". Fix: a `<p>` after the h1.

#### M-6. Static 404 has an `h3` as its only heading; Error page shows template copy to readers

`src/Croft.Web/Components/Blog/NotFoundContent.razor:14`:
`<h3 class="not-found__title">This page has wandered off</h3>` is the only heading on the
page (the `FocusOnNavigate Selector="h1"` in `Routes.razor` therefore finds nothing to focus).
`src/Croft.Web/Components/Pages/Error.razor:12,22`: `MudText Typo="Typo.h3"` followed by
`<h2>Development Mode</h2>` and the stock Blazor template text. Fix: an `h1` on both; strip
the template paragraphs from Error.

#### M-7. Tag remove button is about 14 px (2.5.8)

`src/Croft.Web/Components/Admin/PostEditorForm.razor:89` and
`PostEditorForm.razor.css:121-128`: `font-size: 14px; line-height: 1;` with no padding gives a
roughly 14 × 14 px "×" button next to the tag text (another target). Fix: `min-width/min-height: 24px`.

#### M-8. Tag entry field has no Enter handler

`PostEditorForm.razor:71`: the tag input (`@oninput` only, line 71-72) is committed only by the "Add tag" button at line 80, so a
keyboard user types a tag, presses Enter, and nothing happens (or the form's default submit
fires). Fix: `@onkeydown` handling `Enter` to add the tag.

#### M-9. `role="toolbar"` without roving tabindex

`src/Croft.Web/Components/Admin/MarkdownEditor.razor:14`:
`<div class="md-editor__toolbar" role="toolbar" aria-label="Formatting">`. The ARIA toolbar
pattern expects a single tab stop with arrow-key movement; here every button is a tab stop,
which contradicts what a screen reader announces. Fix: either remove the role (a plain group
of buttons) or implement the roving tabindex.

#### M-10. `alertdialog` that is neither modal nor focused

`src/Croft.Web/Components/Admin/DraftRestorePrompt.razor:7`:
`<div class="draft-restore" role="alertdialog" aria-labelledby="draft-restore-text">`. Nothing
moves focus into it and it does not trap focus, so screen-reader users may never hear the
restore offer. Fix: `role="status"` or `aria-live="assertive"` for a passive banner, or make it
a real dialog with focus management.

#### M-11. Views chart has no text alternative

`src/Croft.Web/Components/Admin/ViewsChart.razor:7`: `<MudChart T="double" … ChartType="ChartType.Line"`
with no table or summary. Fix: a visually hidden `<table>` of date/count, or `aria-describedby`
pointing at a textual summary.

#### M-12. Two `h1`s on the post editor with preview

`src/Croft.Web/Components/Pages/Admin/PostEditPage.razor:65,71` render `<PageSurface …>` /
`<PostHero …>`, each of which renders its own `h1`, next to the page's own `PageHeader` h1.
Fix: a `HeadingLevel` parameter on the preview components, or wrap the preview in an `<iframe>`/
`role="region" aria-label="Preview"` and accept the duplicate.

#### M-13. `#blazor-error-ui` dismiss is a `span`

`src/Croft.Web/Components/Layout/MainLayout.razor:48`: `<span class="dismiss">🗙</span>`. Not
focusable, no accessible name, and the container has no `role="alert"`, so when the circuit
dies the message is neither announced nor dismissible by keyboard. Fix: `<button type="button" aria-label="Dismiss">`
and `role="alert"` on the container.

#### M-14. `aria-label` on the post-card link overrides its content

`src/Croft.Web/Components/Blog/PostCard.razor:8`:
`<a class="post-card__link" href="…" aria-label="@Post.Title">`. The label replaces the h3,
summary and meta that are inside the link, so screen readers hear only the title; the summary
and date are lost. Also a 2.5.3 Label in Name risk if the visible h3 ever differs from `Title`.
Fix: drop the `aria-label` and let the content name the link.

#### M-15. `color-scheme: light` forced on the search box

`src/Croft.Web/Components/Pages/Search.razor.css:46`: `color-scheme: light;` inside
`.search-field__box`. In dark mode the native clear button and caret of `type="search"` render
for a light scheme on a dark field. Fix: `color-scheme: light dark`.

#### M-16. Schedule is disabled with no explanation

`PostEditorForm.razor:19`: `disabled="@(!ScheduleEnabled || ContentOverLimit)"` on Schedule.
When no date is set, the button is simply inert; nothing tells the author why. Fix:
`aria-describedby` to a hint, or keep it enabled and validate on click.

#### M-17. Focus style for form fields on static pages depends on the browser default

`app.css:126` (quoted in I-1) excludes `input`/`select`/`textarea`. Static pages have only the
search field, which has its own focus style via `Search.razor.css`, so no current failure; any
new field on a reading page gets the UA default, which the brief's `KeyboardFocusTests` (2 px,
3:1) would not accept in every browser.

## Checked and found sound

- Landmarks: `SkipLink` as the first focusable, `<header>` top bar, `<nav aria-label="Main navigation">`,
  `<main id="content" tabindex="-1">`; the skip link's target takes focus.
- `TopBar` menu button: `aria-controls`, `aria-expanded`, `aria-label`; `shell.js` focus trap
  (lines 183-204), Escape closes and focus returns (`closeNav`); `LayoutTests:245-279` cover it.
- Contents panel: Escape, focus return (`focusPanelBack`), `aria-expanded` kept in sync,
  `prefers-reduced-motion` honoured (`ContentsPanel.razor.css:255`); `MobileContentsPanelTests` cover it.
- Scroll spy sets `aria-current="true"` on the active TOC link; pager uses real links with
  `aria-current="page"`; decorative SVGs carry `aria-hidden="true"`; body links underlined (W-039).
- `FocusOnNavigate Selector="h1"` with `h1:focus { outline: none }` (W-012): acceptable, the
  focus is programmatic and every reading page except 404 (M-6) has an h1.
- Text contrast, default and example palettes, both themes: every text/background pair the
  stylesheets combine is at or above 4.5:1 (lowest: muted on surface in the example dark
  palette, 4.95:1). Status badges 6.6 / 8.4 / 5.7 / 4.95. Syntax colours in
  `highlight-theme.css`: comments 4.66 (light code background) / 5.23 (dark), the rest higher.
  Table and TOC hairlines at 1.3:1 are decorative, not boundaries.
- No JavaScript: the search is a plain GET form, pager links are real links, the TOC renders
  untruncated, the drawer is open by default in the sidebar layout, the theme follows
  `prefers-color-scheme`.
- `shell.js`: `initialized` guard against double registration, delegated listeners, all
  `localStorage` access wrapped in `try/catch`.
- `post.js`: highlighting builds DOM nodes rather than `innerHTML`; `decorateImages` is
  idempotent; `teardownPost()` removes the scroll/resize/panel listeners (W-085) and is
  called on every non-post boot; the module-level `focusin` listener (line 691) is registered
  once per module instance, which is once per document.
- `recordPostView` guards `navigator.sendBeacon` and sends one beacon per boot.
- `ReconnectModal` uses `<dialog>` with `showModal()`, so it is modal and focus-trapped by the
  browser.
- `DraftReviewPage` shows `ex.Message` on `SessionExpiredException` rather than a spinner
  (line 120-124).
- `IJSObjectReference` disposal for the shell module: already raised as PR #4 M-20; not repeated.
