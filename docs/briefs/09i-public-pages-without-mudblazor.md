# Brief 09i: the reading pages load no MudBlazor

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item,
TDD, break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:** the seven reading pages look exactly as they do today, in light and
dark, at every size, but a reader no longer downloads MudBlazor's stylesheet (625 KB uncompressed)
and script (70 KB) on them. Admin, the editor, login/logout and the error and not-found pages
(the interactive ones) keep MudBlazor unchanged.

**Evidence:** after brief 09f the seven reading pages (Home, Post, Category, Tag, Categories,
StaticPage, Search) are static SSR. Their entire MudBlazor use is:
- one `MudProgressCircular` per page;
- one `MudIcon` in Search;
- the Search field wearing MudBlazor class names and CSS copied from MudBlazor.min.css;
- one `var(--mud-palette-text-primary)` in `Search.razor.css`;
- the four providers in the shared `MainLayout` (`MudThemeProvider`, `MudDialogProvider`,
  `MudSnackbarProvider`, `MudPopoverProvider`).

`TopBar`, `SidebarNav`, `ThemeSwitch` and the `Components/Blog` surfaces use no MudBlazor
components. Yet `App.razor` links `MudBlazor.min.css` and `MudBlazor.min.js` on every document.
09f also had to append MudBlazor's dark palette to `/branding/theme.css`
(`Theme/MudDarkPaletteCss`), only because static pages can't flip `MudThemeProvider`.

## Item 1: static documents carry no MudBlazor

**Do:**
- `App.razor` links `MudBlazor.min.css` and `MudBlazor.min.js` only when `PageRenderMode` is
  interactive. `data-render` already tells the two kinds of document apart.
- `MainLayout` renders the four MudBlazor providers only on interactive documents (the same
  `HttpContext.AcceptsInteractiveRouting()` decision, cascaded or recomputed; no second rule).
  The sidebar's theme handling on static pages stays with `static-glue.js`.
- Replace the reading pages' MudBlazor components with plain markup and our own CSS tokens:
  - `MudProgressCircular`: first find out whether it can still render on a static page. Static
    SSR without streaming rendering awaits the data before sending HTML. If it can't render,
    delete it and its loading branch; if it can, a plain CSS spinner.
  - Search's `MudIcon`: an inline SVG with the same path.
  - Search's field: own class names and CSS in `Search.razor.css`, using our tokens instead of
    MudBlazor's classes, copied declarations and `--mud-palette-*`. The `:has(...)` label shrink
    stays. It keeps the GET form, `name="q"`, the submit button, `data-enhance`, the Escape
    handling, the hidden WebKit decorations, accessibility (label, focus ring, contrast in both
    themes) and the look.
- **MudBlazor's base CSS:** MudBlazor.min.css also carries a reset and element defaults
  (box-sizing, margins, `a`, `button`, typography) that the reading pages may rely on without
  knowing it. Find what they rely on by comparing computed styles and screenshots with and
  without the stylesheet. Add only what's needed to `app.css`, using our tokens. Don't copy
  MudBlazor CSS wholesale. List every rule you add and why in the commit.

**Tests:**
- **E2E:** each of the seven pages loads no `MudBlazor.min.css` and no `MudBlazor.min.js`
  (Playwright request log) and its HTML contains no `mud-` class. An admin page and `/not-found`
  still load both and still render MudBlazor components.
- **The look:** the existing `ScreenshotTests`, all sizes, light and dark, pass unchanged. No
  baseline may be re-recorded in this brief; a changed baseline is a failure to fix. Also the
  dark and light axe tests, the CSP tests (zero violations), and `dev/screenshot-gate` (README)
  if it applies to these pages.
- **Unit:** the Search field markup has no MudBlazor class and no style attribute.
- **Break-it-once:** link MudBlazor.min.css unconditionally again; the no-MudBlazor test fails.
  Then remove one rule you added to app.css; a screenshot test fails (name it).

## Item 2: MudBlazor's dark palette leaves theme.css

**Do:** static pages render no MudBlazor component that reads `--mud-palette-*` any more, and
interactive pages have `MudThemeProvider`, which handles dark mode itself, as before 09f. So the
appended block from 09f is dead weight: remove `MudDarkPaletteCss`, its startup build and its
`WORKAROUND` markers. `ThemeStylesheet` keeps serving theme.css with `?v=` as the hash of the
exact served text (09f's fix). If anything on a static page still needs a `--mud-palette-*`
variable, stop and report it instead of removing the block.

**Tests:**
- 09f's dark-palette unit test changes to assert theme.css carries **no** `--mud-palette-`
  declaration. Say so in the commit: the brief changed the rule.
- The cache-buster tests still pass.
- Dark mode on the static pages (screenshots, axe) and on an admin page (`MudThemeProvider`
  dark, an existing dark admin test or a new one) is unchanged.
- **Break-it-once:** append the old block again; the "no --mud-palette-" test fails.

## Item 3: nothing else regressed

**Do:** the full suites; E2E twice at the end. Measure the reading pages' transfer before and
after: the total bytes of all requests for a cold load of Home and of a post (Playwright, cache
disabled, compressed sizes as served by the dev stack). Put the numbers in the commit.

## Workarounds

09h (running in parallel, phase 2 after this brief) owns `docs/workarounds.md`. Don't edit it.
In your completion report, list which `WORKAROUND(` markers you removed or changed and which
existing workarounds this brief made unnecessary on the reading pages. Expected: the Search
field's MudBlazor clothing, the dark-palette append, and possibly the typography `!important`
overrides, if they only mattered with MudBlazor's stylesheet loaded (they stay if admin needs
them). Mark any new workaround as 09f did (`WORKAROUND(<library>)` … `END WORKAROUND`, no ID).

## Out of scope

- Admin, the editor, login/logout, error and not-found pages: unchanged, with MudBlazor.
- The CSP contributors for MudBlazor's style blocks (they stay; interactive documents need them).
- Removing MudBlazor from the project, or changing its version.
- Any visual change. If the pages can't look identical without MudBlazor's stylesheet, stop and
  report what differs.

## Owner decisions (2026-10-16)

Both differences below were accepted after the task review flagged them under the "no visual change" rule. No code change.

- The static 404's Home and Search links no longer show MudBlazor's press ripple (it is script-driven; a static document loads no MudBlazor script).
- `#blazor-error-ui` on static documents shows readable `--text` on light yellow instead of MudBlazor's white on light yellow.
