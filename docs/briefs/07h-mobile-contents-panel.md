# Brief 07h: on phones and tablets, "On this page" stays at hand while reading

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review. Starts after brief 07e is accepted (07e fixes
the scroll-spy in `wwwroot/js/post.js`; build on that fix). Brief 07g (pages) may run at the same
time; it doesn't touch the post page's contents or `post.js`. If brief 07f (CSP) has been accepted
when you merge, its policy applies: **no `style="..."` attributes on public pages**; set sizes from
JS through the CSSOM (`element.style.x = ...`) or with classes, and add the panel's open and closed
states to `CspTests`.

**Owner's report (2026-10-11):** on a phone, "On this page" is shown at the top and scrolled past
immediately. Today below 1000px of content width it is a collapsed `<details class="toc-inline">`
above the text (`Pages/Post.razor:42`), and the 56px top bar is not sticky
(`TopBar.razor.css`), so once reading neither is on screen. The Tags panel sits after the text.

**Design (owner, 2026-10-11),** at content widths where the rail is not shown (below the
`@container (min-width: 1000px)` rule), on posts with at least two headings:

1. **Near the top:** the panel sits in the flow right under the hero: "On this page" with up to
   five entries, and the post's tags in a column on the right of the same panel. When the post
   has more than five sections, the panel says so (owner: "as long as there are ellipses or some
   other indication that it is not complete, and it is clickable to expand"): after the fifth
   entry a "+N more…" line (N = the hidden count), a button that opens the panel (state 3)
   with the full list.
2. **While reading:** as soon as the panel would scroll off the top, it pins to the top of the
   viewport (`top: 0`) and shrinks to **one row**: the current section's title (the scroll-spy's
   `aria-current` entry; ellipsis when long) and a chevron. Row height ≥ 44px (touch target),
   at most 48px. It never covers text it doesn't own: headings scrolled to land below it
   (`scroll-margin-top` equal to the row height).
3. **Opened:** tapping the row, or pulling its handle down, opens the panel: the full list
   (scrolling inside, height at most `70vh`) with the current entry scrolled into view and
   highlighted, tags on the right. Tapping an entry scrolls to that section and closes the panel;
   tapping outside it, pulling it up, or Escape closes it. The row is a `button` with
   `aria-expanded` and `aria-controls`; closing returns focus to it.
4. **Tags:** in the panel only. The Tags panel after the text stays for posts without a contents
   panel (fewer than two headings) and is not shown for posts with one.
5. Motion: open/close and shrink animate in ≤ 200ms; with `prefers-reduced-motion` they don't
   animate. At rail widths nothing changes (07d's sticky rail).

Both pages again: `Pages/Post.razor` and `Pages/Editor/DraftReviewPage.razor` (deliberately
duplicated layout; on the review page the Feedback panel stays where it is now).

## Item 1: the panel's three states

Implement the above. Keep one source of truth for the list (the existing `TableOfContents`
component) and one scroll-spy (`post.js`); the collapsed row reads the spy's current entry.

Tests (E2E; each test creates its own post: 12 h2 sections of ~150 words and 3 tags; plus one with
no headings), at 390×844 and 768×1024:
- before scrolling: the panel is below the hero, shows the "On this page" heading, exactly five
  entries, the line "+7 more…", and the three tags right of the list (each tag's left ≥ the
  list's right); tapping "+7 more…" opens the panel with all 12 entries; a post with 4 sections
  shows all 4 and no "more" line;
- after scrolling to section 6: the panel's top is 0 ±1, its height is 44–48px, its text is
  section 6's title, which is the `aria-current` entry; text above the fold is not covered (the
  first visible paragraph's top ≥ the panel's bottom when it's below a heading you jumped to);
- tap the row: `aria-expanded="true"`, the list and the tags are visible, the current entry is
  inside the list's visible box; tap entry 9: section 9's heading top is within 8px below the
  panel's bottom, the panel is closed, its text is section 9's title;
- pull the handle down 200px with touch (Playwright touchscreen or pointer events with
  `pointerType: touch`): opens; pull up: closes; Escape closes and focus is on the row button;
- the post with no headings has no panel and its Tags panel is after the text;
- at 1440×900 the page is as 07d left it (its tests pass unchanged).
Break-it-once: remove the pinning (`position: sticky`/the pinned class); the "top is 0" test
fails. Remove `scroll-margin-top`; the "heading below the panel" test fails.

## Item 2: screenshots for the owner

Before/after screenshots at 390×844 of: the top of a post, mid-read (collapsed row), opened, in
light and dark, attached to the completion report (not committed). The screenshot comparison of
every other page against `blog-framework` before this brief stays pixel-identical (report the
counts).

## Out of scope

The desktop rail, the top bar, the home and list pages, the TOC's content (which headings are
listed), anything in `Croft.Api`.
