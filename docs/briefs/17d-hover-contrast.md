# Brief 17d: hover contrast of the red text buttons, and a hovered state in the matrix (issue #29)

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once. No pushes except
`brief/17d` to `origin` and ONE draft PR. E2E instances 4..5 are yours
(`dev/run-e2e.sh --instances 2 --first-instance 4`). Other briefs run at the same time:
`pgrep -af "dotnet test|run-e2e"` (excluding your own shell) before every suite run; never run a
full suite while another one runs; wait and retry.

## The finding, and what may already be true

The brief 12e review (2026-10-23) measured the red **Delete** text button on `/admin/tags` at
2.97:1 while the pointer rested on it (WCAG AA needs 4.5:1). Since then W-095 landed in 0.6.8
(`src/Croft.Web/wwwroot/app.css:93-105`: `--error-text` `#C62828` light / `#FCA5A5` dark on
`.mud-button-text.mud-button-text-error:not(:disabled)`), and it only sets the **label** colour.
MudBlazor's hover rule stays: `.mud-button-text.mud-button-text-error:hover { background-color:
var(--mud-palette-error-hover) }` (MudBlazor 9.11.0, `MudBlazor.min.css`), the error colour at the
palette's `HoverOpacity` over the surface. On paper the W-095 label on that tint is about 5.2:1
(light) and 7.3:1 (dark), so the 2.97 may be gone, or may come from a different node than the
label (the tooltip wrapper `.admin-table__actions`, a disabled button, the focus ring). **Measure
first; the number decides the fix.** Nothing in the suite scans a hovered state today
(`AccessibilityMatrixTests.cs` scans "page" and, below 600 px, "drawer open"; the dialog test
`ATextVariantErrorButtonInADialog_HasAtLeast45To1Contrast` at :346-379 reads the colour at rest).

The buttons concerned (all `MudButton Color="Color.Error"`, default `Variant.Text`):
`Pages/Admin/TagsPage.razor:72-76` (categories) and `:142-146` (tags), `Admin/MediaGrid.razor:83`
and `:90`, `Admin/HashtagListEditor.razor:26-27`. Enabled only when the row has no posts
(`Disabled="@(row.Category.PostCount > 0)"`), so the fixture needs a tag and a category with no
posts.

## Item 1: measure, then fix if needed

Write the hovered-state test first (item 2's `hover` state for `/admin/tags`), run it in both
themes at 1440 × 900, and record for the enabled Delete button: the computed `color`, the
`background-color` composited over its ancestors (`ColorContrast.BackdropOf`, `Composite`,
`Ratio` in `tests/Croft.Web.E2E/ColorContrast.cs`), the ratio, and axe's `color-contrast` nodes if
it reports any. Put the four numbers (light/dark × label/backdrop) in the commit message.

- If any pair is below 4.5:1: fix it in `app.css` next to W-095 (same workaround entry, extended:
  the hover/focus background or the hovered label colour; the CSP hash is not affected, `app.css`
  is a file, not inline style), update `docs/workarounds.md` W-095 and
  `tests/Croft.Web.Tests/PaletteGuardTests.cs:216-238` (add the hover case: the label colour on
  the error colour at MudBlazor's `HoverOpacity` over Bg and Surface, both modes; read the opacity
  from MudBlazor's `Palette` defaults, do not guess it, and say where you read it).
- If every pair is at or above 4.5:1: the fix is W-095 (already shipped); say so with the numbers,
  and item 1's commit is the measurement test plus the `PaletteGuardTests` hover case (which must
  fail if `--error-text` is set back to MudBlazor's default red `#F44336`: break-it-once).

Either way, the dialog test at :346-379 gains a hovered variant (same button, `HoverAsync`, same
4.5 threshold).

## Item 2: the matrix scans one hovered state per admin route that has a hover target

`AccessibilityAdminMatrixTests` (`AccessibilityMatrixTests.cs:246-295`): add a state `hover` for
routes with a destructive text button: `tags` (the enabled tag Delete; the fixture creates a tag
and a category with no posts, so both buttons are enabled; hover the tag row's button) and
`media` (a tile's Delete). Pointer viewports only (1440 × 900; the 390 context has `HasTouch` and
no hover). Hover with `Locator.HoverAsync()` on the button itself (enabled buttons take the hover;
the `.admin-table__actions` position trick in `AdminTests.cs:276` is for disabled ones), then
`RunScanAsync(page, "hover: Delete")`. The scan asserts through `AssertScans` like the others
(any violation fails; `Known` stays empty). Break-it-once: add a temporary
`.mud-button-text-error:hover { color: #F44336 }` to `app.css`, the hover scan must fail on
`color-contrast`; revert.

Keep the public matrix as it is (reading pages have no hover-only colours by design; a hover state
there is out of scope).

## Item 3: suites
`dotnet build Croft.slnx`; `dotnet test tests/Croft.Web.Tests --no-build`; E2E through
`dev/run-e2e.sh --instances 2 --first-instance 4` (full, because `AccessibilityMatrixTests` and the
dialog tests are in the main pass). API suite only if you touched `src/Croft.Api` (you should not).
Summary lines verbatim into the last commit message and the report.

## Report
The measured numbers (light/dark, label/backdrop, ratio) before and after; which branch of item 1
applied; the break-it-once evidence for items 1 and 2; suite lines; the PR's `ci` run URL. What is
left undone.

## Out of scope
Other hover states (links, nav), the public pages, focus-ring styling, MudBlazor upgrades.
