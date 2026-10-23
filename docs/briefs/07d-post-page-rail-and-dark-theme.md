# Brief 07d: the post page's rail stays in view, related posts at the end, long titles stay readable, dark admin restored

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review. Paths are after brief 07c (`src/Croft.Web`).

## Item 1: the dark MudBlazor palette is a PaletteDark again (regression from 07a)

**Owner-visible now** (screenshots, 0.1.5 vs today, `/admin/posts` at 1440×900 in dark): the
table's row dividers and outline are bright light-grey lines, the search field's outline is light
grey, and the pager's arrow buttons are almost invisible. Light mode and every public page are
pixel-identical to 0.1.5.

**Cause (confirmed by reading the code):** `Theme/BlogTheme.cs` builds both modes with one
`private static PaletteLight Palette(BrandPalette)`, so `PaletteDark` is a `PaletteLight` and every
property the theme does not set takes MudBlazor's **light** default (`TableLines` `#e0e0e0`,
`LinesInputs` `#bdbdbd`, `ActionDefault` `rgba(0,0,0,.54)`, ...). 0.1.5 used `new PaletteDark { ... }`.

**Fix:** build `PaletteLight` for light and `PaletteDark` for dark, with the same token mapping
(a generic `T Palette<T>(BrandPalette) where T : Palette, new()` is fine).

Tests (unit, `BlogThemeTests`): `Create(branding).PaletteDark` is a `PaletteDark`; for each of
`TableLines`, `LinesDefault`, `LinesInputs`, `Divider`, `ActionDefault`, `ActionDisabled`,
`TextDisabled`, `OverlayDark`, `Skeleton` its value equals `new PaletteDark()`'s (they are not set by
the branding); the mapped properties still carry the branding's dark tokens (existing tests).
Break-it-once: build the dark palette as `PaletteLight` again; the type test and the value tests
must fail.

## Item 2: "On this page" and Tags stay in view

**Now:** the rail (`Components/Blog/PostRail.razor`) stacks On this page, Tags, Related, then the
caller's panels; it scrolls away with the page, so on a long post the table of contents is gone
after the first screen.

**Design (owner, 2026-10-10):** at content widths where the rail shows (the `@container
(min-width: 1000px)` rule in `Post.razor.css`), the rail's top group is **Tags, then On this page**
(Tags above), and that group is `position: sticky` with its top edge **24px** below the viewport's
top while the page scrolls (the window scrolls; no top bar exists at these widths, it shows only
below 960px of viewport). Related leaves the rail (item 3). Caller panels (`ChildContent`, the
draft review page's Feedback) render **above** the sticky group, so nothing ever sits below it
for it to slide over. When the group is taller than the viewport minus 48px, the group's height is
capped at `calc(100vh - 48px)` and the table of contents scrolls inside it (Tags stay at the top),
and the scroll-spy's active entry (`aria-current="true"`, `wwwroot/js/post.js`) is scrolled into
view within the TOC (`block: 'nearest'`) when it changes. Below 1000px nothing changes: the
inline collapsed "On this page" stays above the text and the rail's panels flow after it.

Both pages: `Pages/Post.razor` and `Pages/Editor/DraftReviewPage.razor` (their layout CSS is
deliberately duplicated; change both together).

Tests (E2E, each test creates its own post through the admin UI as now):
- a post with 12 h2 sections of ~150 words each and 3 tags, at 1440×900 and 2560×1440: after
  scrolling to the last section, the Tags panel's top is 24px ±1 below the viewport top and the
  TOC is fully inside the viewport; the Tags panel is above the TOC (its bottom ≤ the TOC's top);
- a post with 40 h2 sections at 1440×900: the group's bottom is ≤ viewport height − 24px ±1, the
  TOC's own box scrolls (`scrollHeight > clientHeight`), and after scrolling the page to section 35
  the `aria-current` link is inside the TOC's visible box;
- the draft review page at 1440×900: the Feedback panel is above the Tags panel, and after
  scrolling to the end the sticky group does not overlap the Feedback panel's box;
- at 390×844: the collapsed inline "On this page" is above the text and no rail element is sticky
  (computed `position` is not `sticky` on any rail element).
Break-it-once: remove `position: sticky`; the 24px test must fail. Remove the in-TOC scroll; the
40-section test must fail.

## Item 3: related posts at the end of the post

**Design:** "Related" becomes a section after the post body, in the text column (same width as the
body text), heading styled like the rail's panel headings. Up to 3 items as cards: the thumbnail
(`BannerOrFallback`, 16:9, full card width, 6px radius) above the title (UI font, 15px,
2 lines max with ellipsis). Three equal columns with a 1.5rem gap when the column is at least
600px wide, one column below that. No related posts: no section, no heading. Below 1000px of
content width the order after the text is: Tags, then Related (then Feedback on the review page).
Both pages again. `RelatedPosts` keeps its parameters; only its layout changes (it has no other
callers; check with grep and say so).

Tests (E2E): a post with 3 related posts (same tag and category; create them) at 1440×900: the
section's top is below the body's last paragraph, its left and right edges equal the body
column's ±1px, the three cards share one row with equal widths ±1px; at 390×844 the cards stack
in one column and the Tags panel is above the Related section; a post with no related posts has no
Related heading anywhere on the page; every card links to its post (follow one; the post loads).
Break-it-once: put Related back in the rail; the column-edges test must fail.

## Item 4: the hero's gradient follows its text

**Now:** the dark gradient over the banner (`BannerOrFallback.razor.css:57`, `linear-gradient(to
top, rgba(0,0,0,.75), rgba(0,0,0,0) 65%)`) covers the bottom 65% of the hero. A long title grows
the hero (brief 03l), so the title's top lines sit on the bare banner: white text on a light photo.

**Measured cause:** at a normal 360px hero a one-line title's top sits about 130px above the
hero's bottom (36% of its height), where the gradient's alpha is 0.75 × (1 − 36/65) ≈ 0.33; on a
white banner that backdrop is sRGB ≈ 170 and white text on it is about 2:1. Long titles are worse
(alpha 0 above 65%). Confirm these numbers on today's code first and report them.

**Design:** the overlay is anchored to the text block instead of the hero's height: alpha 0.75 at
the hero's bottom (unchanged), at least 0.55 everywhere behind the text block (from the hero's
bottom to the top of the text block's first element), fading linearly to 0 over 4rem above the
text block. Short-title heroes therefore get a slightly darker band behind the title; that is
intended. Implement it in `PostHero` (e.g. a pseudo-element on `.post-hero__content`, behind the
text, extending to the hero's bottom and sides); `BannerOrFallback`'s own overlay stays for the
cards that use it and is turned off for the hero only.

Why 0.55: white text needs 4.5:1, i.e. a backdrop of relative luminance ≤ 0.183 (1.05 / 4.5 −
0.05), which is sRGB ≤ 118 per channel; on a white banner alpha a gives 255 × (1 − a), so a ≥ 0.54.

Tests (E2E, create the posts; a pure white 1600×900 banner uploaded by the test):
- titles of 20, 120 and 300 characters with a subtitle, at 2560×1440, 1440×900 and 390×844: in
  the test, set the title's and subtitle's `color` to `transparent` (so glyphs don't affect the
  sample), screenshot the hero, and sample the pixel at (title box left + 2px, middle of the
  title's first line, from `getClientRects()[0]`): every channel ≤ 118;
- the same 300-character hero: the pixel 4rem + 8px above the text block's top (when that is
  still inside the hero) has every channel ≥ 245 (the fade ends; the banner shows);
- report before/after screenshots of the 20- and 300-character heroes at 1440×900 in the
  completion report (the owner judges the look); don't commit baseline images.
Break-it-once: restore the fixed 65% gradient; the 300-character sample at 2560×1440 must fail.

## Out of scope

Highlight-theme colours, the sidebar, card layouts on the home page, new palette tokens, the
editor, anything in `Croft.Api`.
