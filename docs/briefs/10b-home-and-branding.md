# Brief 10b: the home page always carries the site's identity

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item,
TDD, break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:**
- The home page shows the site's name and description over its tartan, whatever banner the
  newest post has.
- The Featured card's image fills the card at every width.
- Card excerpts never end in ".…".
- A fresh Croft install shows the Black Watch tartan instead of the slate stripes.

**Evidence (the owner's screenshot of crawford.dev, 2026-10-18, a wide desktop window):**
- **No site identity on the home page:** the newest post, "Load Testing the Blog", has its own
  banner (a dark chart). The only large image on the home page is now that chart. Before, the
  newest post had no banner, so the Featured card showed the site's tartan, which is what branded
  the page.
- **The Featured card's image** fills only about 65 % of the card's width; the rest is white.
  The media box measured 765 × 328 screen px: exactly 21:9.
- **The card's excerpt** ends "…the whole stack myself.…": a full stop followed by "…".

## Item 1: the Featured card's image fills the card

**Cause (confirm with the test first):** `PostCard.razor.css` gives
`.post-card--featured .post-card__media` `aspect-ratio: 21 / 9` and `max-height: 420px`, but no
width. With an automatic width, CSS carries the maximum height through the ratio into a maximum
width of 420 × 21 / 9 = 980 px, so on any card wider than 980 px the media box stops at 980 px.

**Do:** `width: 100%` on the featured media box, so the width comes from the card and the height
is min(width × 9 / 21, 420 px). The image keeps `object-fit: cover`. Keep the phone's 4:3 rule
unchanged, and the fallback tartan unchanged.

**Tests (E2E, the Featured card with a banner image):**
- At 1440 × 900 and 1920 × 1080: the media box's width equals the card's inner width (± 1 px),
  and its height is at most 420 px.
- At 390 × 844 the box is 4:3 (± 1 px) and full width.
- **Break-it-once:** remove `width: 100%`; the 1920 case must fail.

## Item 2: excerpts never end in ".…"

**Cause:** `PostSummaryExtractor.Truncate` (Croft.Api) cuts at the last space before 300
characters and always appends "…".

**Do:**
- When the cut text ends with sentence-final punctuation (`.`, `!`, `?`, or one of these followed
  by a closing quote or bracket), end it there without "…".
- Otherwise keep today's cut and "…".
- The stored `Summary` must change for existing posts: the existing `PostSummaryBackfillService`
  fills only `Summary == null`. Add a migration that sets `Summary` to null for every post, so the
  backfill recomputes it at the next start. Check that the backfill's batches, and its cache
  notification, cope with every post at once.

**Tests (API):**
- The owner's paragraph, from "I built my cluster…" to "…Network, OS, Software.", produces a
  summary ending "…the whole stack myself." with no "…".
- A cut in mid-sentence still ends in "…".
- `?`, `!` and `."` endings.
- The migration plus the backfill recompute an existing post's summary.
- **Break-it-once:** always append "…" again; the first test must fail.

## Item 3: a site masthead on the home page

**Design (owner-approved 2026-10-17):**
- **Where:** a slim strip above "Latest Posts", on **page 1 only**. Not on `?page=2` and later,
  or any other page.
- **What:**
  - the background is the site's fallback banner, the same `--fallback-banner` image and
    `--fallback-wash` as the cards' fallback, tiled the same way;
  - over it, the site **name** and **description** (the description falls back to the tagline,
    as `Home.razor`'s `Description` does);
  - **no logo**, because the sidebar already shows it;
  - white text, over a dark gradient like the Featured card's overlay
    (`.banner-or-fallback--overlay::after`).
- **Height:** about 180 px from 960 px wide and about 120 px below it. Text that would overflow
  wraps; it never truncates the name.
- **Headings:** the site name is the home page's `h1`, and "Latest Posts" becomes an `h2`.
  - `PageHeader` gets a heading-level parameter, defaulting to `h1`, so the other pages are
    unchanged.
  - Check `SeoHead` and the accessibility tests for any "one h1" assumption.
- **Configuration**, in `branding.json` / `Branding`:
  - `Masthead`, a boolean defaulting to true;
  - `MastheadBannerFile`, optional. When unset, the masthead uses the fallback banner. When set,
    it is served exactly like `FallbackBannerFile`, with the same validation, content-hash
    cache-busting and theme token, as its own `--masthead-banner`.
  - Document both in the branding docs or README, wherever `FallbackBannerFile` is documented.
- **Static and CSP-clean:** a plain component, with no MudBlazor, no script, and no inline
  `style` attributes (the image comes through a theme token). The CSP doesn't change.
- **Reduced motion and print:** nothing animates. In print the masthead is hidden, if the
  existing print styles hide decoration; otherwise leave it.

**Tests:**
- **E2E:** with the newest post having its own banner, the masthead is above the Featured card
  and shows the configured name and description.
- **Height bounds:** 170–190 px at 1440 × 900, and 110–130 px at 390 × 844.
- **Contrast:** the name and description over the masthead are at least 4.5:1, computed against
  the wash and gradient as composited behind the text, in light and dark themes. Reuse the
  alpha-aware compositing from item 5; if item 5 isn't done yet, do it first.
- **Where it doesn't appear:** none on `/?page=2`, none on a category page, and none with
  `Masthead: false` (unit or bUnit).
- **Headings:** the home page has exactly one `h1`, whose text is the site name.
- **Break-it-once:** remove the masthead from `Home.razor`; the placement test must fail.

## Item 4: Croft's default fallback banner becomes the Black Watch tartan

**Design (owner-approved 2026-10-16):**
- Replace `wwwroot/branding-default/fallback-banner.svg` (slate stripes) with the
  **Government / 42nd / Black Watch** sett.
- **Why this tartan:** it is universal by custom. The Scottish Register of Tartans dates it to
  1739, designer unknown, so it carries no copyright. Its navy, green and black suit the default
  slate and blue palette.
- **Thread count:** the Register's half-sett, reference 277, "Grant Hunting or Black Watch", as
  reproduced at tartanflag.com:
  `DB/44 K4 DB4 K4 DB4 K32 G32 K4 G32 K32 DB32 K4 DB/4`. The slashes are pivots: the sett runs to
  the end, then mirrors back.
- **Build it the way the Crawford tartan is built.** The site repository's
  `fallback-banner.svg` isn't in your worktree, so this is the method:
  - one square sett tile, with the warp drawn as horizontal bands, the weft as vertical bands
    under a 45° twill mask (a `pattern` of 1-unit stripes rotated 45°, used as a `mask`);
  - one SVG unit per thread, or half a thread, if that keeps the tile under 512 units;
  - tiled by the existing `background-size: 180px` rule.
- **Colours** (modern colourway, close to the Register's):
  - dark blue `#1C2841`;
  - black `#0E0E10`;
  - green `#1F4730`.
  The owner checks them in the screenshots. Keep them in one place at the top of the SVG, or
  generate the file with a small script under `dev/`, committed with it.
- **Credit:** a comment in the SVG naming the sett, its Register reference and the thread
  count's source.
- **Size:** at most 8 KB. No external references, scripts or styles.

**Tests:**
- The default banner is served with the right content type, and its hash changes, so every
  cached copy updates.
- **A pixel test** (`PngPixelDecoder` exists): render the default fallback on a list card in a
  site with **no** branding folder, and sample the three colours at known offsets within one
  sett, with the twill mixing allowed for.
- **Screenshot baselines:** re-record only the "no site banner, default branding" cases, and say
  so in the commit. That's an owner-approved visual change; any other baseline that changes is a
  bug.
- **Break-it-once:** put the old SVG back; the pixel test must fail.

## Item 5: two minors from 09i's final review

- **`KeyboardFocusTests`' contrast check:**
  - make it alpha-aware: composite each semi-transparent background over the opaque backdrop
    beneath it (walk up to the first opaque ancestor, then composite down);
  - check the focus outline against **both** sides: the backdrop outside the element, and the
    element's own fill.
  - Item 3's contrast test reuses this helper; put it where both can use it.
  - **Break-it-once:** paint a focusable control's surroundings with a translucent wash that
    passes today's alpha-blind check but fails the true composite. The test must fail, then
    remove the probe.
- **`Branding.PaletteHash`** has no production use since `ThemeHash` was retired. Remove it and
  its test, unless a production use exists. If one does, say where in the commit and leave it.

## Out of scope

- Per-banner focal points (`object-position`) for the Featured crop.
- Any editor or session change (brief 10a), and CSP changes (brief 10c).
- crawford.dev's own branding folder: the site keeps the Crawford tartan.
