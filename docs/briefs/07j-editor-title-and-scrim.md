# Brief 07j: the editor names what it edits, and the scrim starts at the drawer's edge

Read AGENTS.md first and follow it. Two small, independent items: implement inline
(`executing-plans`), one commit per item, TDD, break-it-once for each key test, E2E twice at the
end, the final whole-branch review. Brief 08a runs at the same time; its item 5 (the editor's draft
backup) may edit `PostEditPage.razor` too, so keep item 1's change there to the title logic. When the orchestrator says another brief was accepted, `git fetch origin` and
`git merge --no-edit origin/blog-framework` (never rebase).

**Owner-visible outcome:** writing a page (About) the editor says "New Page" / "Edit Page", not
"Post"; on a phone, opening the contents drawer dims exactly the page below the drawer.

## Item 1: the editor's title follows the kind

**Now:** `Components/Pages/Admin/PostEditPage.razor:15` and `:17` set the browser title
(`BlogPageTitle`) and the heading (`PageHeader`) to `_isNew ? "New Post" : "Edit Post"`, on all
three routes, so `/admin/pages/new` and the edit route of a page both say "Post". The kind is
already known: `Kind` / `IsPage` (`:90-93`; the loaded post's `Kind` on edit routes,
`IsNewPageRequest` on `/admin/pages/new`).

**Do:** title and heading are "New Post" / "Edit Post" for a post, "New Page" / "Edit Page" for a
page. On an edit route, before the post has loaded, the kind is unknown: show "Edit" alone, so
the wrong noun never appears (no "Edit Post" flash on a page).

**Tests (E2E, each creating its own post/page):** `/admin/pages/new` shows "New Page" in the
heading and the document title; `/admin/posts/new` "New Post"; the edit route of a page "Edit
Page"; of a post "Edit Post". Plus a bUnit/unit test that the heading before load is "Edit".
Break-it-once: restore the `_isNew` ternary; the page tests fail.

## Item 2: the scrim's top edge is the drawer's bottom edge after it opens

**Now (brief 07i, `wwwroot/js/post.js`):** `applyPanelState()` sets the panel's height and then
calls `syncPanelScrim()`, which reads `panelEl.getBoundingClientRect().bottom`. The panel's height
has a 200 ms transition (`ContentsPanel.razor.css:29`), so the read returns the height at the
start of the transition. The scrim's top stays there until the next scroll. 07i's own screenshot
at 390×844 (light, opened from the pinned row) shows the scrim from y≈241 while the drawer's
bottom is at y≈383: the 16 px strips beside the drawer are dimmed 140 px higher than specified.
Confirm this cause before fixing (log both values in a quick probe).

**Do:** re-sync the scrim when the panel's height transition ends (`transitionend` for
`height` on the panel), as well as where it syncs now. Under reduced motion there is no
transition and the immediate read is already right; it must stay right. No style attributes
beyond the CSSOM `top` it sets today (CSP).

**Tests (E2E, `MobileContentsPanelTests`, at 390×844 and 360×740, light):** open the drawer from
the in-flow block and from the pinned row (scroll to mid-post first), do not scroll, wait 300 ms:
the scrim's top equals the drawer's bottom (±1 px). Same with reduced motion emulated. Break-it-once:
remove the `transitionend` re-sync; the non-reduced-motion cases fail.

## Facts

- E2E: `dev/run-e2e.sh --instances 3 --first-instance 5` (07i used 5-7 and has finished; check
  `ss -ltn` anyway).
- The `test-runner` subagent may still be on an exhausted model: run builds and suites yourself,
  output to a file, read the summary lines. Do not use `pattern-applier`.

## Out of scope

Anything else in the editor or the contents panel; 08a's files (the token store, Data Protection,
health, the editor's draft backup).
