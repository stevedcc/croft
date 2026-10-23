# Brief 13g: accessibility and front-end robustness (WCAG 2.2 AA review)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for each key test,
E2E twice at the end, the final whole-branch review.

**Source:** `docs/reviews/2026-10-19-accessibility-frontend-review.md` (committed with this
brief). Line numbers in it are from Croft 0.6.2; briefs 13c–13h have moved some since. Read
each finding completely before its item: it has file:line, the measured ratio or size, and the
fix. Where it offers two fixes, this brief chooses.

**Owner-visible outcome:** every public page and every admin page passes an automated WCAG 2.2 AA
scan at desktop and phone width in both themes, with moderate violations failing the suite; a
keyboard or screen-reader user can do everything a mouse user can in the editor and the
categories/tags page; a failed script import never leaves a page half-initialised.

**CSP impact: none.** No new script, style source or inline handler. Any change that would need
one is out of scope: stop and report.

**Item 1 first.** The scan matrix is the measuring instrument for most other items: build it,
record which of its cases fail today (that list is the red state for items 2-5), then fix.

## Item 1: the scan covers every page, both widths, both themes (I-11, and the guard for the rest)
- `AccessibilityTests`: one `TestCase` matrix over every public route (Home, Post, Categories,
  Category, Tag, Search with and without a query, a static page, 404) and every admin route
  (Posts, post editor, editor list, editor review, Media, Categories & Tags, Pages, Analytics, a
  post's analytics page) at 1440 × 900 and 390 × 844, light and dark theme.
- Moderate violations fail too. Known exceptions go in one explicit allow-list (rule id + route +
  a reason + the item that removes it); the allow-list must be empty at the end of this brief, or
  each remaining entry is reported with why.
- Enable `target-size` (2.5.8) and keep `color-contrast`; check in the test that the axe build
  actually runs `target-size` (a rule that never ran must fail the test, not pass it).
- At 390 px, open the drawer and the inline contents panel before scanning (they only exist there).
- Open each MudDialog the admin uses once (delete confirmations, media picker) and scan it.
- Break-it-once: put `role="textbox"` back on the search input (item 3) → the Search case fails.

## Item 2: focus is always visible and never lost (I-1, I-5, M-17, M-10)
- Add `input, select, textarea` to the global focus-visible selector in `app.css:126` (not
  per-component rules), and give the two stretched file inputs (`BannerPicker`, `MarkdownEditor`)
  a `:focus-within` outline on their wrapper matching the sibling buttons.
- Categories/Tags rename: focus moves into the field when Rename is activated; Enter commits,
  Escape cancels and returns focus to the Rename button. The reason a Delete is disabled is
  visible text (or `aria-describedby` to visible text), not only a tooltip.
- `DraftRestorePrompt`: `role="status"` with `aria-live="polite"` (passive banner), not
  `alertdialog`.
Tests (E2E): tabbing onto each file input shows an outline ≥ 2 px at ≥ 3:1 against its
background (reuse `KeyboardFocusTests`' measurement); Rename → the focused element is the text
field; Enter saves (API shows the new name), Escape restores the old name and focus is on Rename.
Break-it-once: drop the wrapper rule → the file-input focus test fails.

## Item 3: the search field is a search field (I-3, M-15)
- `--field-line` ≥ 3:1 against the light background (`rgb(118 118 118)` or darker, work it out
  from `BrandPalette` defaults and the example palette and show the ratio in the commit).
- Remove `role="textbox"`, `aria-invalid`, `aria-required`, `maxlength`, the button's
  `tabindex="-1"`, the icon's `role="img"`; the decorative fieldset gets `aria-hidden="true"`.
- `color-scheme: light dark` on the box.
Tests: the input's computed role is `searchbox` (Playwright `GetByRole(AriaRole.Searchbox)`), the
submit button is reachable with Tab, the line's contrast ≥ 3:1 in both themes (computed from the
rendered colours, not the CSS text).

## Item 4: targets are at least 24 px (I-2, M-7)
- `.category-chip` `min-height: 24px`; the tag remove button `min-width/min-height: 24px`.
Tests: bounding boxes ≥ 24 × 24 CSS px at 390 px width (Playwright), and the target-size rule of
item 1 passes on those pages.

## Item 5: names, roles, headings and landmarks (I-6, I-7, M-1, M-3, M-4, M-5, M-6, M-9, M-12, M-13, M-14)
- Posts grid: the category name next to its icon (visually hidden if the design keeps icon-only);
  row buttons named with the post title (`Delete <title>`).
- Contents-panel toggle: constant accessible name "Contents" server-side; the section title stays
  visible text.
- Footer outside `<main>` (keep `role="contentinfo"`), sidebar group headings not `h2` before the
  page `h1` (`aria-labelledby` on the nav groups), "12 posts" for counts, page meta as a `<p>`
  after the `h1`, an `h1` on the 404 and the Error page (strip the Blazor template copy from
  Error), no `role="toolbar"` on the editor toolbar (plain group, every button a tab stop), one
  `h1` on the post editor (a `HeadingLevel` parameter on the preview components), the Blazor error
  UI's dismiss is a `<button aria-label="Dismiss">` in a `role="alert"` container, no `aria-label`
  on the post-card link.
Tests: mostly the item 1 matrix (heading-order, landmark rules, button-name), plus explicit
assertions for the accessible names (Playwright `GetByRole` with the expected name) for the grid
buttons, the contents toggle before `post.js` runs (block the script), and counts.

## Item 6: status changes are announced (I-4, M-8, M-16, M-11)
- One always-rendered `role="status"` region in the post editor that receives "Saved HH:mm" and
  the publish/unpublish/schedule outcomes.
- Enter in the tag field adds the tag (and does not submit the form).
- Schedule's disabled state is explained by visible text linked with `aria-describedby`.
- The views chart gets a visually hidden table of date/count.
Tests (E2E or bUnit): after Save, the status region's text is "Saved HH:mm"; after Publish it
says so; Enter in the tag field adds a pill and no request to the save endpoint is made; the
chart's hidden table has one row per day with the same numbers as the chart's data.

## Item 7: palette guard covers what the CSS combines (I-8, I-9)
- `PaletteTokens.ContrastPairs` lists every foreground/background pair the stylesheets combine
  (derive the list by searching the CSS for each token's uses; the review names five missing),
  plus non-text pairs at 3:1 (the search line, borders that are boundaries).
- A test fails if a CSS file uses a token pair that is not in the list (parse the CSS for
  `color`/`background` token uses per rule, or keep a reviewed allow-list with the reason).
- Hero subtitle at full opacity white (or raise the gradient mid-stop; pick one and show the
  worst-case ratio ≥ 4.5:1 over a white banner in the commit).
Break-it-once: remove one pair from the list → the CSS-pairs test fails.

## Item 8: a failed import never leaves the page half-initialised (I-10, M-2)
- `static-glue.js` `boot()`: teardown before the import, the post branch in `try/catch` with
  `console.error`, `data-interactive="on"` in `finally`.
- One global `prefers-reduced-motion: reduce` rule in `app.css` that removes transitions and
  limits animations (drawer, reconnect modal, search).
Tests (E2E): route `post.js` to a 500 → the page still reaches `data-interactive="on"`, the theme
switch works, a console error names the failure; navigate from a post to another post → the old
page's scroll handler no longer runs (count calls via an exposed test hook or a spy on
`blogPost.teardownPost`). With `reducedMotion: reduce` the drawer's computed
`transition-duration` is ~0.
Break-it-once: move `data-interactive` back out of `finally` → the failed-import test fails.

## Out of scope
- Replacing MudBlazor on public pages (its own brief).
- New features or visual redesign; colours change only where a ratio requires it.
- Anything in 13b, 13e or 13f.
