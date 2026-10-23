# Brief 03m: a consistent vertical rhythm in posts, and heroes that fit long titles

Read AGENTS.md first and follow it, including "Finishing a task". One commit per item, TDD,
break-it-once, E2E twice in a row at the end (through `dev/run-e2e.sh`), then the final
whole-branch review. Both defects were seen on the live site (0.1.4).

## Item 1: the post body's vertical rhythm

In `.markdown-body` (`src/Blog.Web/Components/Blog/PostBody.razor.css`), paragraphs have no
margin (the global reset zeroes it and nothing sets it back), so separate paragraphs run together;
lists got `margin: 1.25rem 0` and `li + li { margin-top: .35rem }` in brief 03l, so a list's items
sit further apart than lines of text and the gap above a list is wider than between paragraphs;
headings rely on defaults.

Set one rhythm, in `em` so it follows the body size (1.125rem, 1.0625rem on phones):

- paragraphs: `margin: 0 0 1em`;
- lists (`ul`, `ol`): `margin: 0 0 1em`; items: no extra space between them (`li + li` margin 0),
  so a list reads like lines of text; nested lists: `margin: 0` (keep 03l's 1.5em indent);
- `h2`: `margin: 1.6em 0 .5em`; `h3`: `margin: 1.3em 0 .4em`; `h4`–`h6`: `margin: 1.1em 0 .3em`;
- `pre`, `table`, `blockquote`, `figure`: `margin: 0 0 1em` (today 1.25rem above and below);
- the body's first child has no top margin and its last child no bottom margin.

Headings keep their sizes and fonts. The draft review page renders `PostBody`, so it follows.

Tests: E2E at 1440px and 390px on your own fixture (two paragraphs, an `h2`, a tight list of
three single-line items, a nested list, an `h3`, a code block): the gap between the two
paragraphs equals 1em of the body font within 1px; consecutive list items are one line-height
apart within 1px (no extra gap); the gap above the `h2` is larger than the gap below it; the
first element starts at the body's top. Break-it-once: put `li + li { margin-top: .35rem }` back;
a test must fail. Screenshots: extend the post screenshots at 390 and 1440px with this fixture.

## Item 2: the post hero fits long titles

With a very long title, the hero's text runs off its top at 2560px and over the banner's edge on
a phone (brief 03l's screenshot fixture shows it): the hero has a fixed height and nothing limits
the title.

- The hero grows to fit its content: today's height becomes a minimum, and the category chip,
  title, subtitle and meta always lie inside the hero's box. No text is hidden or clamped.
- A long unbroken word wraps rather than overflowing (`overflow-wrap: anywhere` on the title and
  subtitle).
- Normal titles look exactly as now (the existing post screenshots are the reference).
- Tests: E2E with a title of about 200 characters and a 60-character unbroken word, at 390px and
  2560px: the title's and the meta line's boxes lie inside the hero's box, and nothing overflows
  horizontally. Break-it-once: restore the fixed height; a test must fail.

## Out of scope

Font sizes, colours, the rail, the home page cards, anything else.
