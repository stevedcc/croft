# Brief 09e: the mobile outline comes back when the reader scrolls back up, smoothly

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item, TDD,
break-it-once for each key test, E2E twice at the end, the final whole-branch review.

**Owner-visible outcome:** on a phone, the "On this page" block at the top of a post pins to the
top as a single row while reading, opens as a drawer on tap or pull, and when the reader scrolls
back up it grows back into the full list in place, with no frame where it covers the post's text,
no empty box and no jump.

**Evidence (2026-10-15):**
- Owner, Safari on iOS, production (Croft 0.5.0): scrolling back up after the outline pinned leaves
  text overlaid; it no longer even clears at the very top (it used to, before).
- Measured in Chromium, 390×844 mobile emulation with touch, interactive and static rendering alike
  (probe `~/sites/observability/loadtest/spike/outline_touch_probe.py`, results `touch-*/log.json`):
  the block is 179 px at the top, 48 px pinned, and after scrolling all the way back up it stays at
  **49 px**: it never re-expands.
- Cause, confirmed by a one-line experiment on a spike branch: `measurePanelFootprint()`
  (`src/Croft.Web/wwwroot/js/post.js`) clears the inline height but measures while the element still
  carries `toc-inline--pinned` (the class that shows only the row); `applyPanelState()` removes the
  class only afterwards. So `updatePanelPin()` re-measures the "natural" height as the row's 49 px.
  With the class removed for the measurement the block came back at 179 px and covered no text.
- The same probe, frames every 30 px around the unpin: the frame before unpinning shows the pinned
  row with an **empty 150 px box** under it (the height animating while the list is hidden), the
  next frame the full list: the jerk the owner sees.
- On a phone the post's tag chip (`#Blazor` in the probe post) draws over the open drawer's top
  right corner (`mobile-outline-open.png`).
- The pull gesture only starts on `.toc-inline__handle` / `.toc-inline__strip`
  (`onPanelPointerDown`); a pull started on the row's text scrolls the page instead.

## Item 1: the outline re-expands after scrolling back up, covering nothing

**Do:** measure the natural block without the pinned/open state applied (the spike removed
`toc-inline--pinned` for the measurement and restored it; any equivalent is fine), and make sure
every path that re-measures (unpin, resize, `document.fonts.ready`) measures the natural block.

**Tests (E2E, `tests/Croft.Web.E2E`, mobile viewport 390×844 with `HasTouch`; the post is created by
the test, with ≥ 7 `##` sections each long enough to scroll):**
- At the top: record the block's height H and the top of the first paragraph after it.
- Scroll down in 30 px steps until the block is pinned (the class), then back up in 30 px steps to
  `scrollY == 0`; after every step: the block's bounding box intersects **no** paragraph or heading
  of the post body that is in the viewport, except while it is pinned (the pinned row may cover the
  page's top 48 px by design, as it does today).
- Back at the top: height == H (±1 px) and the first paragraph's top equals its first value (±1 px).
- The same in the open state: open the drawer while pinned, scroll back to the top, close it: back
  to H, nothing covered.
- Break-it-once: revert the measurement fix; the "back at the top: height == H" test fails.

## Item 2: no empty box and no jump at the unpin

**Now:** while still pinned the block's height starts animating (200 ms) towards the natural height
with the list hidden, so one or more frames show an empty box; then the list appears at once.

**Do:** design the transition so that at every frame the block shows either the pinned row or the
list, never an empty area larger than 8 px, and the change from row to list is animated (height and
the list's opacity, 200 ms; none under `prefers-reduced-motion: reduce`). Pick the approach
(e.g. un-pin first, then animate the height from the row's to the natural height with the list
fading in) and say why in the commit.

**Tests:** frame sampling as in item 1 around the unpin (scroll in 10 px steps across the 300 px
before and after the pin threshold, one screenshot-free DOM sample per animation frame for 300 ms
after each step via `requestAnimationFrame`): in no sample is `blockHeight − rowHeight` > 8 px while
the list's computed `visibility` is hidden or its `opacity` < 0.05. With reduced motion emulated, the
height switches in one frame. Break-it-once: restore the old ordering; the sampling test fails.

## Item 3: the tag chip stays under the open drawer

**Do:** the open drawer stacks above the post's tags (and everything else in the article) on
phone and tablet widths.

**Tests:** E2E at 390×844 and 768×1024: post with tags and ≥ 7 sections, pinned and opened: the
element at the drawer's top-right corner (`document.elementFromPoint`, 8 px inside) belongs to the
drawer. Break-it-once: drop the stacking rule; the test fails.

## Item 4: pulling the outline open works where a reader would pull

**Do:** decide with evidence whether the pull should start anywhere on the pinned row (a reader
pulls where the text is) or only on the handle and strip as today. If anywhere on the row: a
vertical pull of ≥ the existing threshold opens it, a tap still opens/closes, a horizontal or
short move does nothing, and page scrolling elsewhere is unchanged. Record the decision in the
commit.

**Tests:** E2E with touch (CDP `Input.dispatchTouchEvent`: start on the row's text, move down
180 px in 9 steps, end): the drawer is open; the same start, moving up when open: closed; a tap:
toggles; a 3 px move: no change. Break-it-once on the pull test.

## Out of scope

- Desktop rail and scroll spy (unchanged).
- Static SSR of the reading pages (brief 09f, after this one; it imports `post.js` unchanged).
- WebKit: the sandbox may not run Playwright's WebKit; the orchestrator re-runs the probe on WebKit
  (iPhone emulation) during verification. If you can run WebKit, add it and say so.
