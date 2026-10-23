# Brief 03f: screen sizes (big screens: content cap and fluid type; phones: the drawer)

Read AGENTS.md first and follow it, including "Finishing a task". Use `subagent-driven-development`
with the items below as the tasks; one commit per item, TDD, break-it-once, E2E twice in a row at
the end, then the final whole-branch review.

**Why:** on the owner's ~2000px-wide desktop, the post page fills less than half the screen: the
hero spans the content area, but the 68ch text column and the 220px rail sit at its left edge with
~850px of empty space to their right (home, category and tag pages likewise). The UI-redesign spec
(§5.4) never said where content sits when there is spare room, and its checks stopped at 1440px.
Apart from the drawer fix in item 4, phones and 1440px must not change: the owner is happy with both.

The owner's decision (2026-10-08): **cap the content area and centre it, and grow the type on big
screens.**

## Item 1: fluid type and a longer line above 1440px

The owner's screen is a 27" 4K monitor at 150% scaling: `innerWidth` 2506px. Bigger letters alone
don't put more text on screen, so both the font size and the line length grow:

| Viewport | Root font size | Text measure |
|---|---|---|
| ≤ 1440px (and phones) | 16px (100%) | 68ch |
| 1920px | about 17.3px | 88ch |
| ≥ 2560px | 19px (118.75%) | 88ch |

- Font size: linear from 16px at 1440px to 19px at 2560px, capped at both ends (one `clamp()` on
  `html`).
- Measure: linear from 68ch at 1440px to 88ch at 1920px, capped at both ends. `calc()` can't divide
  a length by a length, so build it from a `ch` part plus a `vw`-proportional `px` part inside a
  `clamp(68ch, …, 88ch)`, or use a few container-query steps; either is fine if the tests pass.
- Sizes that should grow with the type use `rem`, not `px`: the post rail's width (220px →
  `13.75rem`) and the cap in item 2. Leave the sidebar's widths and the layout breakpoints in px;
  they are about the viewport, not the text.
- Tests (E2E, computed styles): root font size 16px at 390px and 1440px, 19px at 2560px and 3000px,
  strictly between at 1920px; the text column is 68ch at 1440px and 88ch (within 1ch) at 1920px and
  2560px. Phones and 1440px unchanged.

## Item 2: cap and centre the content area

- A content wrapper inside `main` with `max-width: 80rem` (1280px at 16px, 1520px at 19px),
  centred (`margin-inline: auto`). Every page renders inside it: home, post, category, tag,
  search, not-found and the admin and editor pages. The hero stays "full content width", which now
  means the wrapper's width.
- On the post page (and the draft review page, which shares its layout CSS; change both files
  together), the text-plus-rail block is centred under the hero inside the wrapper, keeping item 1's
  text measure and the container-query switch at 1000px.
- Tests (E2E at 1920px and 2560px): the wrapper is at most 80rem wide and centred in `main` (left and right
  gaps equal within 2px); the post's text-plus-rail block is centred under the hero (within 2px);
  the text column has item 1's measure. At 1440px and 390px every existing layout assertion still
  passes unchanged. Break-it-once: remove the centring from the post body; the centring test fails.

## Item 3: screenshots at 1920px and 2560px

Add 1920px and 2560px to `ScreenshotTests` (Home, Post, Posts list, Editor, light and dark), next to the
existing 1440 / 1100 / 390. They stay review-only. Make sure the captures include a post with a
banner and enough text to show the column.

## Item 4: the phone drawer reaches its last item

On the owner's iPhone 11 (Safari), the drawer opened with ☰ shows the theme switch as its lowest
entry: "Log in" / "Log out" below it is hidden under Safari's toolbar, and the drawer can't be
scrolled. On desktop "Log in" shows below the theme switch.

- Size the drawer with the dynamic viewport height (`100dvh`, with `100vh` before it as the
  fallback), let it scroll vertically when its content is taller than the screen
  (`overflow-y: auto`, with the page behind it not scrolling while it's open), and pad its bottom by
  `env(safe-area-inset-bottom)` so the last item clears the home indicator (`viewport-fit=cover`
  in the viewport meta if that's needed for the inset to apply).
- E2E: on a 390 × 500 viewport, open the drawer with ☰, scroll the drawer, and click "Log in"; the
  mock IdP's login page appears. Break-it-once: remove the drawer's `overflow-y`; the test fails.

## Out of scope

Changing the measure below 1440px, the sidebar's widths and breakpoints, the phone layout beyond item 4, colours, anything else.
