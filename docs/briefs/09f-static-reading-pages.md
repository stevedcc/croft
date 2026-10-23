# Brief 09f: the reading pages are static HTML; only admin and the editor open a circuit

Read AGENTS.md first and follow it (the "Interactivity" section changes in item 1). Implement
inline (`executing-plans`), one commit per item, TDD, break-it-once for each key test, E2E twice
at the end, the final whole-branch review.

**Owner-visible outcome:** a reader's page looks and behaves exactly as today (menu, theme,
outline, scroll spy, search, smooth link navigation), but costs croft-web no Blazor circuit and no
WebSocket. A burst of visitors costs page renders only, which 09b/09d made cheap; circuits remain
for admin and the editor.

**Evidence:**
- Ignitor reload test 2026-10-15: circuits, not CPU or the database, killed croft-web
  (`~/sites/observability/loadtest/results/2026-10-15-ignitor-reload-*`); brief 09g limits them,
  this brief removes them from readers.
- Spike, same day: branch `spike/static-ssr` (never merge; read it, don't merge or cherry-pick:
  build it again here with tests), findings in that branch's
  `docs/spikes/2026-10-15-static-ssr-reading-pages.md`. A Playwright probe passed 15/15 checks with
  these changes: no WebSocket on reading pages, no console errors, theme switch, sidebar collapse,
  mobile menu, scroll spy (also after enhanced navigation), mobile outline (follows, expands,
  jumps, closes), enhanced navigation, search via `?q=`, one page-view beacon per post view.
- Why things broke before the fixes: `shell.js` and `post.js` are started over JS interop from
  `OnAfterRenderAsync` (`MainLayout.razor:75`, `Post.razor:129`), which a static page never runs;
  the theme switch is a Blazor `@onclick`; search posts back through `@oninput`/`@onclick`; the
  page-view beacon is sent from `Post.razor`'s `OnAfterRenderAsync`.

## Item 1: the seven reading pages render as static SSR

**Do:** `Components/App.razor`: `<Routes @rendermode="PageRenderMode" />` with `PageRenderMode =>
HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null` (a `[CascadingParameter]
HttpContext`); `@attribute [ExcludeFromInteractiveRouting]` on Home, Post, Category, Tag,
Categories, StaticPage, Search. Admin, editor, login/logout and Error/NotFound keep today's mode.
Remove `[PersistentState]` (brief 09c) from those seven pages: a page that never becomes
interactive has nothing to restore, and the state comment costs bytes on every page (MainLayout
keeps its own, it also renders admin). Update AGENTS.md "Interactivity" to describe the split.

**Tests (E2E):** each of the seven pages opens **no** WebSocket to `/_blazor` (Playwright
`page.on("websocket")`) and has no `<!--Blazor:` server marker; an admin page still connects a
circuit. Navigation static → admin (link click) starts a circuit; admin → a reading page shows the
reading page correctly (a full load is fine, say which happens). Break-it-once: remove the
attribute from Post; the Post test fails.

## Item 2: the reading pages' JavaScript starts without a circuit

**Do:** one module (spike: `wwwroot/js/static-glue.js`; name it for what it does) loaded by
`App.razor` that, on first load **and** after every enhanced navigation (Blazor's
`enhancedload`), starts `shell.js` (`init`, idempotent) and, on post pages, `post.js`
(`highlightAll`, `decorateImages`, `scrollToHash`, `initScrollSpy`, `initContentsPanel`), and
handles the theme switch's click (it calls `cycleTheme` and updates the button's label exactly as
the Blazor handler did; interactive pages keep the Blazor handler, so don't run both). Repeated
enhanced navigations must not stack listeners (each init tears down the previous one, as
`initContentsPanel` already does; check `initScrollSpy` and fix it if it doesn't).

**Tests (E2E, desktop 1440×900 and mobile 390×844 with touch):** theme switch cycles and its label
follows; sidebar collapse; mobile menu opens and closes; scroll spy marks exactly one link in the
rail and one in the panel; mobile outline follows scrolling, expands on tap, a link jumps and
closes it; code blocks are highlighted. Then 5 enhanced navigations between two long posts and
back: the same checks still pass, and a scroll fires the spy's handler once (count calls with a
test-only spy wrapper installed from the test, not from `src/`). Break-it-once: don't re-run init
on `enhancedload`; the after-navigation test fails.

## Item 3: search is a GET form

**Do:** wrap the search field in `<form method="get" action="/search" role="search">`, `name="q"`,
the adornment button `type="submit"`; the server already reads `?q=` and `page`. Keep the
MudBlazor-styled markup. Fix the header's "1 results" (singular/plural).

**Tests (E2E):** type a term and press Enter: URL has `?q=term`, results show, no full reload
(window marker survives); the button submits the same; paging links keep `q`; "1 result" vs
"2 results". Break-it-once: remove `name="q"`; the search test fails.

## Item 4: the page-view beacon works on static pages

**Do:** `Post.razor` renders the post's id on the post layout (`data-post-id`); the module from
item 2 sends `blogAnalytics.recordPageView(id)` once per post view, including views reached by
enhanced navigation, never twice for one view, never on non-post pages.

**Tests (E2E):** count POSTs to `/api/analytics/pageview`: one per full load of a post, one per
enhanced navigation to another post, zero for home/category/tag/search; the API's page view
count for the post goes up by exactly the number of views. Break-it-once: send on every
`enhancedload` regardless of page type; the zero-on-home test fails.

## Item 5: nothing else regressed

**Do:** run the full E2E suite; adapt tests that relied on reading pages being interactive (e.g.
waiting for `data-interactive="on"`: keep that signal meaningful, set by the item 2 module once
the page's JS is ready). List every test you changed and why. Check enforced CSP (the CSP E2E
category) and dark mode on a post, a list and search (screenshots in the commit's evidence).

## Item 6: a tap on the outline row still counts when the finger wanders a little

**Evidence:** WebKit re-probe of the accepted 09e code, 2026-10-16 (iPhone 13 emulation,
`~/sites/observability/loadtest/spike/outline_touch_probe_webkit.py`, output in
`spike/webkit-accepted-09e/`): a touch tap on the pinned row whose finger moves 6–7 px before
lifting leaves the drawer closed (`slop-tap-open: exp=false`); a still tap opens it. Cause:
`onPanelPointerDown` (`post.js`) marks any move of ≥ 3 px as a drag, and a drag consumes the click
that follows. iOS still fires that click (its tap slop is about 10 px), so the reader's tap is
swallowed.

**Do:** for `pointerType === 'touch'` the move that makes a gesture a drag is ≥ 10 px (Euclidean
distance from the pointerdown point); mouse and pen keep 3 px. The pull threshold (48 px) is
unchanged.

**Tests (E2E, mobile 390×844 with touch, pinned row):** dispatch touch-type pointer events
(`pointerdown`, `pointermove`s, `pointerup`, then `click`) as the probe does: a 6 px wander
toggles the drawer open and a second one closes it; a 12 px wander (no pull) changes nothing; a
60 px downward pull still opens it exactly once; a mouse-type gesture with a 6 px move still
changes nothing. Break-it-once: put the touch threshold back to 3 px; the 6 px test fails.

## Out of scope

- The mobile outline's other behaviour (brief 09e, already merged before this one; `post.js`
  comes in as 09e left it, apart from item 6).
- Circuit limits (09g), admin/editor render modes, WebAssembly/Auto, output caching.
