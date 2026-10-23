# Brief 07i: the mobile contents panel, polished; tags in a stable order

Read AGENTS.md first and follow it. One commit per item, TDD, break-it-once for every key test,
E2E twice at the end, the final whole-branch review. Builds on brief 07h (accepted, `dda5228`).
Briefs 07f (CSP), 07g (pages) and 08b (HA) are running: 07g also edits `PostService` and the
post hero's date line, 07f adds `CspTests`. When the orchestrator says one of them was accepted,
`git fetch origin` and `git merge --no-edit origin/blog-framework` (never rebase), keep both
sides, re-run the suites. 07f's policy forbids `style="..."` attributes on public pages: style
through classes or the CSSOM only.

**Owner's review of 07h's screenshots (2026-10-11, 390×844):** three things, plus one found by
07h's agent.

## Item 1: the panel's three fixes (`ContentsPanel.razor(.css)`, `post.js`)

1. **Gap under the hero.** The "ON THIS PAGE" label touches the hero's bottom edge (no space).
   Make the space between the hero's bottom and the panel's top equal to the space between the
   hero's bottom and the first line of the text on a post **without** a panel (measure that
   value; don't guess it), and the space between the panel's bottom (the "+N more…" line or the
   last entry) and the text the same again.
2. **A visible handle when opened.** The opened drawer ends with no padding and nothing showing
   it can be pulled up. Add a grab bar centred at its bottom edge: 36×4px, fully rounded, the
   muted text colour token, inside a strip ≥ 24px tall that is the pull-up target (the existing
   pull-to-close gesture moves to it; the row's pull-down stays). 12px between the last list
   entry and the strip. The pinned row (closed) shows no bar.
3. **Dim the page behind the opened drawer.** While open, a scrim covers the page below the
   drawer: black at 32% opacity in light mode, 48% in dark (CSS tokens on the theme, not literal
   values in the component), and the drawer gets the theme's existing elevation shadow if there
   is one (else `0 4px 16px` black 24%). Tapping the scrim closes (as "tapping outside" does
   now). Fades with the open/close animation (≤ 200ms), none with `prefers-reduced-motion`. The
   scrim is not focusable and is `aria-hidden`; the page behind keeps scrolling disabled only if
   07h already disables it (don't add scroll locking).

Tests (E2E, 390×844 and 768×1024, light and dark where colour is asserted; each test creates its
own post with 12 sections and 3 tags, plus one post with no headings for the comparison):
- gap: |(panel top − hero bottom) − (text top − hero bottom on the no-headings post)| ≤ 1px, and
  the same for panel bottom → text top;
- opened: the grab bar is visible, 36×4 ±1px, horizontally centred in the drawer ±1px, its strip
  ≥ 24px tall, ≥ 12px below the last entry's bottom; pulling the strip up 150px with touch closes
  (focus on the row button); closed/pinned: no bar visible;
- scrim: opened, a point in the text area below the drawer shows the scrim (the element at that
  point is the scrim, and its computed background alpha is 0.32 light / 0.48 dark ±0.01);
  tapping it closes; closed: no scrim in the hit test;
- 07h's tests pass unchanged.
Break-it-once: remove the gap rule; the gap test fails. Remove the scrim element; the scrim test
fails.

## Item 2: a post's tags in a stable order (`Croft.Api`)

**Now:** `PostService` maps `p.Tags` with no ordering (`PostService.cs` lines ~353 and ~403), so
a post's tags come back in a different order per request; 07h's agent proved it with self-diffs.
**Do:** every API response that carries a post's tags lists them by name, case-insensitive
ordinal (`StringComparer.OrdinalIgnoreCase`; sort where the DTO list is built, not in SQL, so the
database collation doesn't matter). Find every place a post's tags reach a DTO (single post,
lists, admin, drafts, related, search; check, don't assume this list is complete) and report it.
Tests (API): a post created with tags in the order `zeta`, `mid`, `Alpha` returns `Alpha`, `mid`,
`zeta` from the single-post endpoint and in the public list, five fetches each. Break-it-once:
remove the sort from the single-post mapping; its test fails.

## Item 3: screenshots for the owner

At 390×844, light and dark: the top of a post, mid-read (pinned row), opened. Put them in
`.superpowers/07i-report/` in your worktree (git-ignored; never `/tmp`, which the orchestrator
can't read) and list them in the completion report.

## Out of scope

The desktop rail, the panel's behaviour beyond the above, tag order on the tag cloud or category
pages (only a post's own tags), anything in `k8s/` or HA.
