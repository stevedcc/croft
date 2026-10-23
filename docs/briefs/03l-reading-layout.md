# Brief 03l: lists inside the column, and a reading column that uses the screen

Read AGENTS.md first and follow it, including "Finishing a task". One commit per item, TDD,
break-it-once, E2E twice in a row at the end, then the final whole-branch review. The owner
reported both defects from the live site (0.1.3): a 27" 4K monitor and an iPhone 11.

## Item 1: list markers inside the text column

In a post, bulleted and numbered lists have no left padding (the global reset sets it to 0 and
`.markdown-body` doesn't set it back), so the markers hang outside the text column: on desktop
they sit left of the paragraph edge, and on a phone they touch or cross the screen edge.

- In `src/Blog.Web/Components/Blog/PostBody.razor.css`, give `ul` and `ol` inside
  `.markdown-body` a left padding (about 1.5em) so the markers sit inside the column, with the
  item text indented from the paragraph edge; nested lists indent one more step; keep sensible
  spacing between items and around the list. Rules reach the rendered Markdown through `::deep`,
  like the file's other rules.
- The draft review page renders `PostBody` too, so it gets the fix with no extra work; check it.
- Tests: an E2E test on a post with a bulleted and a numbered list (your own fixture) that the
  list's marker area lies inside the text column at 390px and at 1920px wide. A list's `li` box
  starts at its content edge, so compare the `ul`/`ol` left edge and its computed
  `padding-left` with the column's left edge (or another concrete measure you can justify).
  Break-it-once: set the padding back to 0; the test must fail.

## Item 2: the reading column is measured in the reading font

`Post.razor.css` sets the text column to `clamp(68ch, ..., 88ch)` on `.post-layout`. `ch` is the
width of "0" in the element's own font, and `.post-layout` has the UI font (Source Sans 3) at
1rem, while the text inside is Source Serif 4 at 1.125rem. So the column holds only about 53
characters of body text per line, and on a large screen the text plus rail fill only about half
of the hero's width.

- Make the measure mean what brief 03f intended: about 68 characters of body text per line at
  ≤1440px, growing to about 88 at ≥1920px, measured in the body text's own font and size
  (`.markdown-body`: `var(--font-serif)`, 1.125rem; 1.0625rem below 960px). How you get there is
  your call (for example, declare the grid in the serif at the body size and keep the rail's own
  fonts explicit), but the rail and TOC must look exactly as they do now.
- When the text-plus-rail block is narrower than the hero at ≥1000px container width, centre it
  under the hero (equal space either side) instead of leaving it at the left. This replaces 03f's
  rule that centred it only when main is at least 80rem. Below 1000px (phones, small tablets)
  nothing changes: the column fills the width as now.
- `DraftReviewPage.razor.css` duplicates this geometry block on purpose (its comment says why);
  change both together.
- Tests: E2E at 1440px and 1920px that a line of body text holds roughly the intended number of
  characters (measure a long paragraph's width against its text, or the column width against the
  serif's `ch`; justify the tolerance), and at 1920px that the block is centred under the hero
  (left and right gaps equal within 2px). Break-it-once: put the measure back on the UI font.
- Screenshots for the owner (the existing screenshot tests, extended): the post page at 1280,
  1440, 1707 (a 4K monitor at 150% scaling), 1920 and 2560px wide, light theme, plus 390px for
  item 1. List the files in the report.

## Out of scope

Font sizes, the hero, the rail's contents, the home page, anything else.
