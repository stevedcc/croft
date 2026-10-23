// Post page behaviour: syntax highlighting (highlight.js — self-hosted core + the supported
// languages, vendored in wwwroot/lib/highlightjs with its licence), the "On this page"
// scroll-spy (IntersectionObserver) and the contents panel's states (pin, open, pull). Loaded
// as a module per page that renders a post body and driven by PostBody
// (highlightAll/decorateImages after each render), the post page and the draft review
// (initScrollSpy/initContentsPanel/scrollToHash).
import hljs from '../lib/highlightjs/core.min.js';
import csharp from '../lib/highlightjs/languages/csharp.min.js';
import javascript from '../lib/highlightjs/languages/javascript.min.js';
import typescript from '../lib/highlightjs/languages/typescript.min.js';
import json from '../lib/highlightjs/languages/json.min.js';
import yaml from '../lib/highlightjs/languages/yaml.min.js';
import bash from '../lib/highlightjs/languages/bash.min.js';
import sql from '../lib/highlightjs/languages/sql.min.js';
import xml from '../lib/highlightjs/languages/xml.min.js';
import css from '../lib/highlightjs/languages/css.min.js';

// The highlight theme ships with the module: it is requested only where a post body is rendered
// (PostBody/Post import this module per page), never app-wide. Without JavaScript the classes it
// colours are never added, so nothing changes for the no-JS render.
if (!document.querySelector('link[data-highlight-theme]')) {
    const link = document.createElement('link');
    link.rel = 'stylesheet';
    link.href = 'css/highlight-theme.css';
    link.dataset.highlightTheme = 'true';
    document.head.appendChild(link);
}

hljs.registerLanguage('csharp', csharp);
hljs.registerLanguage('javascript', javascript);
hljs.registerLanguage('typescript', typescript);
hljs.registerLanguage('json', json);
hljs.registerLanguage('yaml', yaml);
hljs.registerLanguage('bash', bash);
hljs.registerLanguage('sql', sql);
hljs.registerLanguage('xml', xml);
hljs.registerLanguage('css', css);
// No maintained Razor/cshtml grammar exists (spec §5.4) — those fences highlight as XML.
hljs.registerLanguage('razor', xml);
hljs.registerLanguage('cshtml', xml);

// Highlights the code blocks under root. Safe to re-run after every interactive render
// (spec §5.4): already-highlighted blocks are left alone.
//
// highlight.js's own highlightElement writes the result with innerHTML, a Trusted Types sink the
// reading pages' report-only policy would flag on every post. The result is built as DOM nodes
// here instead (see appendHighlighted), with what highlightElement leaves on the element: the
// hljs class, the language class and data-highlighted.
function highlightAll(root) {
    (root ?? document).querySelectorAll('pre code').forEach(block => {
        if (block.dataset.highlighted) {
            return;
        }
        highlightBlock(block);
    });
}

function highlightBlock(block) {
    const language = [...block.classList]
        .map(name => /^(?:lang|language)-(.+)$/.exec(name)?.[1])
        .find(name => name !== undefined);
    if (block.classList.contains('nohighlight') || block.classList.contains('no-highlight')) {
        return;
    }
    const code = block.textContent;
    const result = language !== undefined && hljs.getLanguage(language)
        ? hljs.highlight(code, { language, ignoreIllegals: true })
        : hljs.highlightAuto(code);
    block.replaceChildren();
    appendHighlighted(block, result.value);
    block.classList.add('hljs');
    if (result.language) {
        block.classList.add('language-' + result.language);
    }
    block.dataset.highlighted = 'yes';
}

// highlight.js returns HTML made only of <span class="…"> … </span> and text in which it has
// escaped & < > " ' as entities. That is turned into nodes without parsing HTML: no innerHTML,
// no DOMParser, no template, so nothing reaches a Trusted Types sink.
const HIGHLIGHT_TOKEN = /<span class="([^"]*)">|<\/span>|([^<]+)|</g;
const HIGHLIGHT_ENTITIES = { amp: '&', lt: '<', gt: '>', quot: '"', '#x27': "'" };

function appendHighlighted(target, html) {
    const open = [target];
    for (const [token, spanClass, text] of html.matchAll(HIGHLIGHT_TOKEN)) {
        const parent = open[open.length - 1];
        if (spanClass !== undefined) {
            const span = document.createElement('span');
            span.className = spanClass;
            parent.appendChild(span);
            open.push(span);
        } else if (token === '</span>') {
            if (open.length > 1) {
                open.pop();
            }
        } else {
            const raw = text ?? token;
            parent.appendChild(document.createTextNode(
                raw.replace(/&(amp|lt|gt|quot|#x27);/g, (_, name) => HIGHLIGHT_ENTITIES[name])));
        }
    }
}

// Spec §5.4 content styling: images are captioned from their Markdown title when present.
// An enhancement only — with JavaScript disabled the images still render, uncaptioned.
function decorateImages(root) {
    (root ?? document).querySelectorAll('.markdown-body img[title]').forEach(img => {
        if (img.closest('figure')) {
            return;
        }
        const figure = document.createElement('figure');
        const caption = document.createElement('figcaption');
        caption.textContent = img.getAttribute('title');
        img.replaceWith(figure);
        figure.append(img, caption);
    });
}

// Scroll-spy for the "On this page" lists (both the rail's and the collapsed block's): an
// IntersectionObserver watches the headings, and each change recomputes which section the
// reader is in (the last heading at or above the top third of the viewport) and marks the
// matching links in both TOCs.
let spyObserver = null;
let spyWaitingForToc = null;
let spyCurrentId = null;
let spyScrollHandler = null;

// WORKAROUND(W-085, Blazor): enhanced navigation disposes no component, so the previous post's scroll listeners must be torn down by hand
// Stops the scroll spy: its observers and its scroll listener. Safe to call at any time.
function teardownScrollSpy() {
    if (spyObserver) {
        spyObserver.disconnect();
        spyObserver = null;
    }

    if (spyWaitingForToc) {
        spyWaitingForToc.disconnect();
        spyWaitingForToc = null;
    }

    // A new page's first marked entry is a fresh start, not a change to keep in view.
    spyCurrentId = null;

    // The scroll listener calls the previous page's update() and must never outlive the TOC it
    // was built for.
    if (spyScrollHandler) {
        window.removeEventListener('scroll', spyScrollHandler);
        spyScrollHandler = null;
    }
}
// END WORKAROUND(W-085)

// WORKAROUND(W-085, Blazor): enhanced navigation disposes no component, so the previous post's scroll listeners must be torn down by hand
// Stops everything this module started for the current page (the scroll spy and the contents
// panel). The static reading pages call it when an enhanced navigation lands on a page without a
// post: nothing must keep listening for a page that is gone.
function teardownPost() {
    teardownScrollSpy();
    teardownContentsPanel();
}
// END WORKAROUND(W-085)

function initScrollSpy() {
    teardownScrollSpy();

    const links = [...document.querySelectorAll('.toc__link')];
    const ids = [...new Set(links.map(link => link.hash.substring(1)).filter(Boolean))];
    const headings = ids.map(id => document.getElementById(id)).filter(Boolean);
    // WORKAROUND(W-011, Blazor): the prerender-to-interactive DOM swap can land after the one-shot call, so the scroll spy waits for the TOC
    if (headings.length === 0) {
        // The TOC is not in the DOM yet. Blazor's prerender → interactive DOM swap can still be in
        // flight when the page's OnAfterRender makes this one-shot call (traced: 0 .toc__link at
        // call time, the nodes arriving milliseconds later), and nothing calls again afterwards —
        // so without waiting the scroll-spy silently never starts on those loads. Wait for the TOC
        // to arrive (a concrete DOM condition) and initialise then; a post with fewer than two
        // headings never gets a TOC and its waiter simply stays dormant until the next navigation.
        if (!spyWaitingForToc) {
            spyWaitingForToc = new MutationObserver(() => {
                if (document.querySelector('.toc__link')) {
                    spyWaitingForToc.disconnect();
                    spyWaitingForToc = null;
                    initScrollSpy();
                }
            });
            spyWaitingForToc.observe(document.body, { childList: true, subtree: true });
        }

        return;
    }
    // END WORKAROUND(W-011)

    const update = () => {
        const cutoff = window.innerHeight * 0.3;
        let current = headings[0].id;
        for (const heading of headings) {
            if (heading.getBoundingClientRect().top <= cutoff) {
                current = heading.id;
            }
        }
        links.forEach(link => {
            const active = link.hash === '#' + current;
            link.classList.toggle('toc__link--active', active);
            if (active) {
                link.setAttribute('aria-current', 'true');
            } else {
                link.removeAttribute('aria-current');
            }
        });

        // The contents panel's pinned row is titled from the entry just marked — one scroll-spy,
        // read wherever its current entry lives.
        syncPanelRow();

        if (current === spyCurrentId) {
            return;
        }
        spyCurrentId = current;

        // The rail's TOC is capped and scrolls inside the sticky group on long posts, so the
        // entry that just became current can be scrolled out of the TOC's own box. Keep it in
        // view within that box (block: 'nearest'), and only then: when the entry is already
        // inside the box nothing must move — in particular not the page itself (the group can
        // sit below the fold while the entry is comfortably in view inside its TOC).
        for (const link of links) {
            if (link.getAttribute('aria-current') !== 'true') {
                continue;
            }
            const box = link.closest('.toc');
            if (!box) {
                continue;
            }
            const linkRect = link.getBoundingClientRect();
            const boxRect = box.getBoundingClientRect();
            if (linkRect.top < boxRect.top || linkRect.bottom > boxRect.bottom) {
                link.scrollIntoView({ block: 'nearest' });
            }
        }
    };

    // The observer above only recomputes when a heading crosses its narrow band (25–35% of the
    // viewport), so a jump scroll — TOC click, fragment link, scrollbar drag — that lands with no
    // heading in the band never fires it and leaves the marking stale (measured: with ~150-word
    // sections a TOC click left the clicked entry unmarked). Recompute on scroll too, at most once
    // per frame; the observer stays because it also reacts to layout changes while nothing scrolls.
    // Named handler, removed before re-add above: post → post navigation reuses the page and calls
    // here again — listeners must never stack.
    let scrollQueued = false;
    spyScrollHandler = () => {
        if (scrollQueued) {
            return;
        }
        scrollQueued = true;
        requestAnimationFrame(() => {
            scrollQueued = false;
            update();
        });
    };
    window.addEventListener('scroll', spyScrollHandler, { passive: true });

    spyObserver = new IntersectionObserver(update, { rootMargin: '-25% 0px -65% 0px' });
    headings.forEach(heading => spyObserver.observe(heading));
    update();
}

// ---------------------------------------------------------------------------
// The contents panel (the rail-less "On this page" block): one panel in three
// states — the in-flow block under the hero, the pinned one-row bar and the
// opened drawer. Everything here is progressive enhancement on top of the
// server-rendered block; without JavaScript the block shows its whole list in
// place. The row's height is 48px, the same number the CSS (ContentsPanel.razor.css)
// gives the row and the pages give the headings' scroll-margin-top.
// ---------------------------------------------------------------------------
const panelRowHeight = 48;

// A pull of at least this many pixels on the row's handle opens or closes the drawer.
const panelPullThreshold = 48;

// WORKAROUND(W-016, Browsers): a finger wanders: iOS still fires the click for a tap that moved up to about 10px
// A touch gesture that moves less than this many pixels is still a tap (see onPanelPointerDown).
const panelTouchDragSlop = 10;
// END WORKAROUND(W-016)

let panelEl = null;
let panelScrim = null;
let panelPinned = false;
let panelOpen = false;
let panelFootprint = 0;
let panelScrollHandler = null;
let panelScrollQueued = false;
let panelResizeHandler = null;
let panelClickHandler = null;
let panelKeyHandler = null;
let panelPointerDownHandler = null;
let panelGestureResetHandler = null;
let panelTransitionHandler = null;
let panelPullConsumed = false;
let panelWaiting = null;

function initContentsPanel() {
    teardownContentsPanel();

    panelEl = document.querySelector('.toc-inline');
    // WORKAROUND(W-011, Blazor): the prerender-to-interactive DOM swap can land after the one-shot call, so the contents panel waits for its element
    if (!panelEl) {
        // The panel can land after this call (the prerender → interactive DOM swap, the race the
        // scroll-spy waits out) and a post with fewer than two headings never gets one at all:
        // wait for it and initialise when it lands.
        panelWaiting = new MutationObserver(() => {
            if (document.querySelector('.toc-inline')) {
                initContentsPanel();
            }
        });
        panelWaiting.observe(document.body, { childList: true, subtree: true });
        return;
    }
    // END WORKAROUND(W-011)

    // The states are an enhancement: without JavaScript the block renders the whole list and
    // nothing is truncated (the CSS keys the truncation and its "+N more…" button on this class).
    panelEl.classList.add('toc-inline--enhanced');

    // The scrim over the page below the opened drawer — the panel's sibling in the same render.
    panelScrim = document.querySelector('.toc-inline__scrim');

    measurePanelFootprint();
    applyPanelState();
    syncPanelRow();

    panelScrollHandler = () => {
        if (panelScrollQueued) {
            return;
        }
        panelScrollQueued = true;
        requestAnimationFrame(() => {
            panelScrollQueued = false;
            updatePanelPin();
            // The page can still scroll while the drawer is open (07h added no scroll locking),
            // so the scrim's top edge — the drawer's bottom — is re-aligned every frame.
            syncPanelScrim();
        });
    };
    window.addEventListener('scroll', panelScrollHandler, { passive: true });

    panelResizeHandler = () => {
        // The block's height follows the viewport (the tags column wraps): re-measure whenever
        // no state change is mid-flight.
        if (panelEl.getClientRects().length > 0 && !panelPinned && !panelOpen) {
            measurePanelFootprint();
            applyPanelState();
        } else {
            // A width change while pinned/open reflows the drawer: its bottom edge (the scrim's
            // top) can move without any state change.
            syncPanelScrim();
        }
    };
    window.addEventListener('resize', panelResizeHandler);

    panelClickHandler = onPanelClick;
    document.addEventListener('click', panelClickHandler);
    panelKeyHandler = onPanelKeyDown;
    document.addEventListener('keydown', panelKeyHandler);
    panelPointerDownHandler = onPanelPointerDown;
    panelEl.addEventListener('pointerdown', panelPointerDownHandler);

    // The panel's height animates (200 ms; none under reduced motion) and the scrim's top edge
    // is the drawer's bottom edge: syncPanelScrim's immediate read sees the height at the start
    // of the transition, so re-sync when the height transition ends (brief 07j item 2). Under
    // reduced motion there is no transition and the immediate read is already right. The panel's
    // margin and the chevron's transform end here too — only the panel's own height moves the
    // bottom edge worth syncing.
    panelTransitionHandler = event => {
        if (event.target === panelEl && event.propertyName === 'height') {
            syncPanelScrim();
        }
    };
    panelEl.addEventListener('transitionend', panelTransitionHandler);

    // A pull leaves no click behind (a touch drag is not a tap): the "this gesture consumed a
    // click" flag is reset by the next pointerdown anywhere, so it can never swallow a later
    // tap outside the panel.
    panelGestureResetHandler = () => {
        panelPullConsumed = false;
    };
    document.addEventListener('pointerdown', panelGestureResetHandler);

    // The block's height depends on the webfonts; a footprint measured on the fallback fonts
    // would clip it once they land.
    document.fonts.ready.then(() => {
        if (panelEl && panelEl.getClientRects().length > 0 && !panelPinned && !panelOpen) {
            measurePanelFootprint();
            applyPanelState();
        }
    });

    updatePanelPin();
}

// WORKAROUND(W-085, Blazor): enhanced navigation disposes no component, so the previous post's panel listeners must be torn down by hand
function teardownContentsPanel() {
    if (panelWaiting) {
        panelWaiting.disconnect();
        panelWaiting = null;
    }
    if (panelScrollHandler) {
        window.removeEventListener('scroll', panelScrollHandler);
        panelScrollHandler = null;
    }
    if (panelResizeHandler) {
        window.removeEventListener('resize', panelResizeHandler);
        panelResizeHandler = null;
    }
    if (panelClickHandler) {
        document.removeEventListener('click', panelClickHandler);
        panelClickHandler = null;
    }
    if (panelKeyHandler) {
        document.removeEventListener('keydown', panelKeyHandler);
        panelKeyHandler = null;
    }
    if (panelEl && panelPointerDownHandler) {
        panelEl.removeEventListener('pointerdown', panelPointerDownHandler);
    }
    panelPointerDownHandler = null;
    if (panelEl && panelTransitionHandler) {
        panelEl.removeEventListener('transitionend', panelTransitionHandler);
    }
    panelTransitionHandler = null;
    if (panelGestureResetHandler) {
        document.removeEventListener('pointerdown', panelGestureResetHandler);
        panelGestureResetHandler = null;
    }
    panelPinned = false;
    panelOpen = false;
    panelFootprint = 0;
    panelEl = null;
    panelScrim = null;
}
// END WORKAROUND(W-085)

// WORKAROUND(W-018, Browsers): the outline measures its natural height with the state classes off, leaving inline height alone so scroll anchoring does not chase a pulse
// The block's natural height: the panel's flow footprint. It must not change while the panel
// shrinks to the row or opens over the text, or everything below the panel would jump. Measured
// on the list block's own box with the state classes off: while toc-inline--pinned is applied
// the row replaces the body and the panel's box would measure the row's height (48px + border)
// as the "natural" block — the bug that left the outline stuck at 49px after scrolling back up.
// The inline height and margin-bottom are deliberately left alone: clearing them (as this used
// to) snaps the height to auto and restarts the margin's transition, pulsing the footprint by
// the row's height for a frame — scroll anchoring chases that pulse and the pin flips back.
// Every caller re-measures through here (unpin, resize, document.fonts.ready), so every path
// measures the natural block.
function measurePanelFootprint() {
    const pinned = panelEl.classList.contains('toc-inline--pinned');
    const open = panelEl.classList.contains('toc-inline--open');
    panelEl.classList.remove('toc-inline--pinned', 'toc-inline--open');
    panelFootprint = panelEl.querySelector('.toc-inline__body').getBoundingClientRect().height;
    panelEl.classList.toggle('toc-inline--pinned', pinned);
    panelEl.classList.toggle('toc-inline--open', open);
}
// END WORKAROUND(W-018)

// WORKAROUND(W-018, Browsers): height and margin-bottom always add up to the footprint, so the drawer overflows its slot instead of moving the text
function applyPanelState() {
    if (!panelEl) {
        return;
    }
    panelEl.classList.toggle('toc-inline--pinned', panelPinned);
    panelEl.classList.toggle('toc-inline--open', panelOpen);
    panelEl.querySelector('.toc-inline__row')?.setAttribute('aria-expanded', String(panelOpen));

    const height = panelOpen ? panelEl.scrollHeight : (panelPinned ? panelRowHeight : panelFootprint);
    // height + margin-bottom always add up to the footprint (the CSSOM is the only place sizes
    // can be written per state without style attributes): the drawer may overflow its slot
    // downward — with a negative margin — instead of moving the text it covers.
    panelEl.style.height = height + 'px';
    panelEl.style.marginBottom = (panelFootprint - height) + 'px';
    syncPanelScrim();
}
// END WORKAROUND(W-018)

// The scrim's top edge is the drawer's bottom edge: set while open (the drawer can open from the
// pinned row at the viewport top or from the block in flow), cleared when closed — the CSS hides
// it then anyway, and a stale top would flash at the next open before this ran.
function syncPanelScrim() {
    if (!panelEl || !panelScrim) {
        return;
    }
    panelScrim.style.top = panelOpen ? panelEl.getBoundingClientRect().bottom + 'px' : '';
}

// The panel pins exactly when its flow position would scroll off the viewport top: sticky holds
// it there and the state shrinks it to the row.
function updatePanelPin() {
    if (!panelEl || panelEl.getClientRects().length === 0) {
        return;
    }
    const pinned = panelEl.getBoundingClientRect().top <= 0.5;
    if (pinned !== panelPinned) {
        panelPinned = pinned;
        if (!pinned && !panelOpen) {
            // A resize or a late font load while pinned can outdate the footprint; the natural
            // block is the truth again once nothing is pinned.
            measurePanelFootprint();
        }
        applyPanelState();
    }
}

function setOpen(open) {
    if (!panelEl || open === panelOpen) {
        return;
    }
    panelOpen = open;
    panelEl.classList.toggle('toc-inline--open', open);
    if (open) {
        syncPanelRow();
        scrollCurrentEntryIntoList();
    }
    applyPanelState();
}

function closePanel() {
    if (!panelEl || !panelOpen) {
        return;
    }
    panelOpen = false;
    panelEl.classList.remove('toc-inline--open');
    if (!panelPinned) {
        // Closing at rest shows the block again: measure its truth now, so a resize or a font
        // load that landed while the drawer was open cannot leave a stale footprint behind.
        measurePanelFootprint();
    }
    applyPanelState();
    // Closing returns focus to the row — the button that opened the drawer — or to the
    // "+N more…" button when the block is what shows after the close. Deferred two frames so
    // an entry tap's fragment scroll lands first: it pins the panel, and the row is what shows
    // then (focusing the button the pin is about to hide would drop the focus).
    requestAnimationFrame(() => requestAnimationFrame(focusPanelBack));
}

function focusPanelBack() {
    if (!panelEl) {
        return;
    }
    const row = panelEl.querySelector('.toc-inline__row');
    const more = panelEl.querySelector('.toc-inline__more');
    const [target, fallback] = panelPinned ? [row, more] : [more, row];
    if (target && target.getClientRects().length > 0) {
        target.focus();
    } else {
        fallback?.focus();
    }
}

// The row's title: the scroll-spy's current entry, read from the DOM it marks.
function syncPanelRow() {
    if (!panelEl) {
        return;
    }
    const current = panelEl.querySelector('.toc__link[aria-current="true"]');
    const rowText = panelEl.querySelector('.toc-inline__row-text');
    const text = current ? current.textContent.trim() : '';
    if (rowText && rowText.textContent !== text) {
        rowText.textContent = text;
    }
}

// The opened drawer shows the full list — the current entry must be inside its visible box.
function scrollCurrentEntryIntoList() {
    const current = panelEl.querySelector('.toc__link[aria-current="true"]');
    const list = panelEl.querySelector('.toc__list');
    if (!current || !list) {
        return;
    }
    const linkRect = current.getBoundingClientRect();
    const listRect = list.getBoundingClientRect();
    if (linkRect.top < listRect.top || linkRect.bottom > listRect.bottom) {
        current.scrollIntoView({ block: 'nearest' });
    }
}

function onPanelClick(event) {
    if (panelPullConsumed) {
        panelPullConsumed = false;
        return;
    }
    if (!panelEl) {
        return;
    }
    if (panelEl.contains(event.target)) {
        if (event.target.closest('.toc-inline__more')) {
            setOpen(true);
        } else if (event.target.closest('.toc-inline__row')) {
            setOpen(!panelOpen);
        } else if (event.target.closest('.toc__link')) {
            // Tapping an entry scrolls to that section (the link's own fragment navigation) and
            // closes the drawer; the footprint rule keeps the heading's landing spot stable.
            closePanel();
        }
        return;
    }
    if (panelOpen) {
        closePanel();
    }
}

function onPanelKeyDown(event) {
    // A keyboard activation (Enter/Space on a button) is a click with no pointerdown before it:
    // nothing may still be holding the "a pull consumed a click" flag from an earlier gesture.
    panelPullConsumed = false;
    if (event.key === 'Escape' && panelOpen) {
        closePanel();
    }
}

// WORKAROUND(W-016, Browsers): pointer gestures on the outline row: a drag consumes the click that follows it, and touch needs a larger slop
// The pull surfaces: anywhere on the row — the text too, not just the handle: a reader pulls
// where the text is (brief 09e item 4; a pull that starts on the row's text used to scroll the
// page instead) — drag down to open the drawer, up to close it (07h's gesture), and, while the
// drawer is open, the grab strip at its bottom edge (drag up to close; the strip's drag down
// does nothing — the drawer is already open). Any pull consumes the click that follows it, so a
// drag never toggles the button twice: a 3px mouse move (10px for a finger) is already a drag
// (not a tap, not a pull) and changes nothing, while a tap still toggles through the row's click.
function onPanelPointerDown(event) {
    panelPullConsumed = false;
    if (!event.target.closest('.toc-inline__row, .toc-inline__strip')) {
        return;
    }
    const startX = event.clientX;
    const startY = event.clientY;
    let moved = false;

    // The move that turns a gesture into a drag (which consumes the click that follows): 3px for a
    // mouse or pen, but a finger wanders — iOS still fires the click for a tap that moved up to
    // about 10px — so a touch gesture is a drag only from 10px (Euclidean) away from where it began.
    const isTouch = event.pointerType === 'touch';
    const onMove = moveEvent => {
        const moveX = moveEvent.clientX - startX;
        const moveY = moveEvent.clientY - startY;
        if (isTouch ? Math.hypot(moveX, moveY) >= panelTouchDragSlop : Math.abs(moveY) >= 3 || Math.abs(moveX) >= 3) {
            moved = true;
        }
    };
    const stop = () => {
        window.removeEventListener('pointermove', onMove);
        window.removeEventListener('pointerup', onUp);
        window.removeEventListener('pointercancel', onCancel);
    };
    const onUp = upEvent => {
        stop();
        const dy = upEvent.clientY - startY;
        if (Math.abs(dy) >= panelPullThreshold) {
            moved = true;
            if (dy > 0 && !panelOpen) {
                setOpen(true);
            } else if (dy < 0 && panelOpen) {
                closePanel();
            }
        }
        if (moved) {
            panelPullConsumed = true;
        }
    };
    const onCancel = () => {
        stop();
        // A cancelled gesture produces no click: nothing may consume the next one.
        panelPullConsumed = false;
    };

    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    window.addEventListener('pointercancel', onCancel);
}
// END WORKAROUND(W-016)

// WORKAROUND(W-012, Blazor): FocusOnNavigate focuses h1 and pulls the viewport off the fragment, so the scroll is asserted again
// Fragment scrolling on load. TOC clicks need nothing here: with the path-qualified
// hrefs Blazor's router recognises a same-page fragment link and scrolls to the heading itself.
// What does need handling is a cold load with #fragment in the URL: the browser scrolls there
// during parsing, but Blazor's FocusOnNavigate (Routes.razor focuses h1) can pull the viewport
// back to the hero afterwards — so the scroll is asserted again here and whenever h1 receives
// programmatic focus while a fragment is set.
function scrollToHash() {
    const id = location.hash ? decodeURIComponent(location.hash.substring(1)) : '';
    if (!id) {
        return;
    }
    document.getElementById(id)?.scrollIntoView();
}

document.addEventListener('focusin', event => {
    if (event.target instanceof HTMLElement && event.target.tagName === 'H1' && location.hash) {
        scrollToHash();
    }
});
// END WORKAROUND(W-012)

window.blogPost = { highlightAll, decorateImages, initScrollSpy, scrollToHash, initContentsPanel, teardownPost };
