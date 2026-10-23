// Splices text in at the textarea's caret (replacing any selection), leaves the caret just
// after the inserted text, and returns the textarea's new value so the bound C# value and the
// DOM never drift apart.
function insertAtCursor(textarea, text) {
    var start = textarea.selectionStart ?? textarea.value.length;
    var end = textarea.selectionEnd ?? start;
    textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
    var caret = start + text.length;
    textarea.setSelectionRange(caret, caret);
    return textarea.value;
}

// Reports the text selected under root at the end of each selection gesture (mouse or keyboard) to
// the draft review page, with the button's position above the selection: top/left in the
// coordinate space of the nearest <main> (which is the containing block for absolutely positioned
// descendants — it carries the layout containment for the post-rail container query).
let stopSelectionWatch = null;

function watchSelection(root, helper) {
    // WORKAROUND(W-011, Blazor): the element reference can arrive without DOM properties during the prerender-to-interactive handoff, so the root is resolved at use time
    // The element reference can arrive without DOM node properties on some navigations (the
    // prerender → interactive handoff), and resolving it here can race the DOM swap — so resolve
    // at use time instead: reports then always see a live root and never throw on a missing one.
    const resolveRoot = () => root instanceof Element ? root : document.querySelector('.draft-review__body');
    // END WORKAROUND(W-011)

    var report = event => {
        // The "Add feedback" button keeps the selection alive across its own mousedown; its
        // mouseup/keyup must not report that selection again after the hand-off consumed it.
        if (event.target instanceof Element && event.target.closest('.draft-review__add-feedback')) {
            return;
        }

        var selection = window.getSelection();
        var text = selection ? selection.toString().trim() : '';
        var anchor = selection && selection.anchorNode;
        var body = resolveRoot();
        if (text.length > 0 && anchor && body?.contains(anchor)) {
            var rect = selection.getRangeAt(0).getBoundingClientRect();
            var anchorRect = (body.closest('main') || document.documentElement).getBoundingClientRect();
            helper.invokeMethodAsync('OnSelectionChanged', text,
                Math.max(8, Math.round(rect.top - 40 - anchorRect.top)),
                Math.max(8, Math.round(rect.left - anchorRect.left)));
        } else {
            helper.invokeMethodAsync('OnSelectionChanged', '', 0, 0);
        }
    };

    unwatchSelection();
    document.addEventListener('mouseup', report);
    document.addEventListener('keyup', report);
    stopSelectionWatch = () => {
        document.removeEventListener('mouseup', report);
        document.removeEventListener('keyup', report);
        document.documentElement.removeAttribute('data-selection-watch');
        stopSelectionWatch = null;
    };

    // Marks the watch as live: the watch starts with the interactive render, so a selection
    // gesture right after a navigation must wait for this before it can be reported. It lives on
    // the document element because it means "the watch is installed" — not "a root resolved this
    // instant", which can legitimately lag the DOM swap.
    document.documentElement.dataset.selectionWatch = 'on';
}

function unwatchSelection() {
    if (stopSelectionWatch) {
        stopSelectionWatch();
    }
}

// Drops the DOM selection — called after the feedback hand-off consumed it, so the next
// keyup/mouseup reports an empty selection instead of resurrecting the button.
function clearSelection() {
    window.getSelection()?.removeAllRanges();
}

// Places the "Add feedback" button at the reported selection (top/left in main's coordinate
// space, CSSOM writes — markup carries no style attribute) and then keeps it 8px inside the
// viewport on every edge. The report's top/left placement only clamps the top-left corner; at
// 390px a selection near the right or bottom edge would otherwise push the button off-screen.
// Runs after Blazor renders the button, so its real size is measurable; the button is absolutely
// positioned in main's coordinate space and stays CSS-hidden until this has placed it.
function placeSelectionButton(top, left) {
    const button = document.querySelector('.draft-review__add-feedback');
    if (!button) {
        return;
    }

    button.style.top = Math.round(top) + 'px';
    button.style.left = Math.round(left) + 'px';
    button.style.visibility = 'visible';

    const rect = button.getBoundingClientRect();
    const anchor = (button.closest('main') || document.documentElement).getBoundingClientRect();
    let dx = 0;
    let dy = 0;
    if (rect.right > window.innerWidth - 8) dx = window.innerWidth - 8 - rect.right;
    if (rect.left + dx < 8) dx += 8 - (rect.left + dx);
    if (rect.bottom > window.innerHeight - 8) dy = window.innerHeight - 8 - rect.bottom;
    if (rect.top + dy < 8) dy += 8 - (rect.top + dy);
    if (dx !== 0) button.style.left = Math.round(rect.left - anchor.left + dx) + 'px';
    if (dy !== 0) button.style.top = Math.round(rect.top - anchor.top + dy) + 'px';
}

// Preview scroll sync (the post editor, from 960px where the Preview is its own scrolling column).
// When the Markdown box scrolls or its caret moves, the Preview scrolls to the block that holds
// the box's top visible source line. The Preview's top-level blocks carry data-source-line (the
// editor's Preview alone asks the renderer for it). One-way: the Preview never drives the box.
// Work is throttled to one run per animation frame.
//
// The box wraps long lines, so a source line's position is not line * lineHeight. A hidden mirror
// (one div per source line, same box metrics as the textarea) tells where each source line
// starts; it is rebuilt only when the text or the box's width changed. The mirror is styled
// through the CSSOM (no inline style or script, so the page's CSP is untouched).
// WORKAROUND(W-089, browser): a textarea reports no position for a source line once long lines wrap, so a hidden mirror measures where each line starts
const syncMirror = { element: null, tops: [], text: null, width: 0 };

function sourceLineTops(textarea) {
    if (syncMirror.text === textarea.value && syncMirror.width === textarea.clientWidth && syncMirror.tops.length > 0) {
        return syncMirror.tops;
    }

    if (!syncMirror.element) {
        syncMirror.element = document.createElement('div');
        syncMirror.element.setAttribute('aria-hidden', 'true');
        document.body.appendChild(syncMirror.element);
    }

    const mirror = syncMirror.element;
    const style = getComputedStyle(textarea);
    Object.assign(mirror.style, {
        position: 'absolute',
        visibility: 'hidden',
        pointerEvents: 'none',
        left: '-9999px',
        top: '0',
        boxSizing: 'border-box',
        width: textarea.clientWidth + 'px',
        padding: style.padding,
        border: '0',
        fontFamily: style.fontFamily,
        fontSize: style.fontSize,
        fontWeight: style.fontWeight,
        lineHeight: style.lineHeight,
        letterSpacing: style.letterSpacing,
        tabSize: style.tabSize,
        whiteSpace: 'pre-wrap',
        overflowWrap: 'break-word',
    });
    mirror.replaceChildren(...textarea.value.split('\n').map(line => {
        const row = document.createElement('div');
        // An empty source line still takes one line of height.
        row.textContent = line === '' ? '\u200b' : line;
        return row;
    }));

    const origin = mirror.getBoundingClientRect().top;
    syncMirror.tops = Array.from(mirror.children, row => row.getBoundingClientRect().top - origin);
    syncMirror.text = textarea.value;
    syncMirror.width = textarea.clientWidth;
    return syncMirror.tops;
}
// END WORKAROUND(W-089)

// The 1-based source line at the top of the box's visible area: the last line that starts at or
// above the visible top (plus the box's top padding and a pixel or two of rounding slack).
function topVisibleSourceLine(textarea) {
    const tops = sourceLineTops(textarea);
    const threshold = textarea.scrollTop + parseFloat(getComputedStyle(textarea).paddingTop) + 2;
    let low = 0;
    let high = tops.length - 1;
    while (low < high) {
        const middle = (low + high + 1) >> 1;
        if (tops[middle] <= threshold) {
            low = middle;
        } else {
            high = middle - 1;
        }
    }

    return low + 1;
}

function syncPreviewToBox(textarea) {
    const side = document.querySelector('.post-edit__side');
    // Under 960px the columns stack and the Preview is not its own scrolling column.
    if (!side || getComputedStyle(side).overflowY !== 'auto') {
        return;
    }

    const line = topVisibleSourceLine(textarea);
    let target = null;
    for (const block of side.querySelectorAll('[data-source-line]')) {
        if (Number(block.getAttribute('data-source-line')) <= line) {
            target = block;
        } else {
            break;
        }
    }

    if (target) {
        side.scrollTop += target.getBoundingClientRect().top - side.getBoundingClientRect().top;
    }
}

let previewSyncQueued = false;

function queuePreviewSync(event) {
    const textarea = event.target;
    if (!(textarea instanceof HTMLTextAreaElement) || !textarea.classList.contains('md-editor__textarea') || previewSyncQueued) {
        return;
    }

    previewSyncQueued = true;
    requestAnimationFrame(() => {
        previewSyncQueued = false;
        syncPreviewToBox(textarea);
    });
}

// Delegated, so the textarea can be re-rendered without re-wiring. scroll does not bubble (hence
// the capture); a caret move without a scroll is a click or a navigation key.
document.addEventListener('scroll', queuePreviewSync, true);
document.addEventListener('click', queuePreviewSync);
document.addEventListener('keyup', event => {
    if (['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End'].includes(event.key)) {
        queuePreviewSync(event);
    }
});

// Puts keyboard focus on the first element matching selector inside the element with the given id
// (the Categories & Tags rename cell moves focus between its text field and its Rename button).
// WORKAROUND(W-094, Blazor): JS interop called from OnAfterRenderAsync can run before the browser has applied the render batch, so the target is looked up again on the next frames
function focusIn(id, selector, framesLeft = 60, framesTried = 0) {
    const target = document.getElementById(id)?.querySelector(selector);
    if (target) {
        target.focus();
    } else if (framesLeft > 0) {
        requestAnimationFrame(() => focusIn(id, selector, framesLeft - 1, framesTried + 1));
    } else {
        // Silence here made a lost focus look like a browser quirk; say what was never found.
        console.warn(`blogEditor.focusIn gave up: no "${selector}" inside #${id} after ${framesTried} animation frames`);
    }
}
// END WORKAROUND(W-094)

window.blogEditor = { insertAtCursor, watchSelection, unwatchSelection, clearSelection, placeSelectionButton, focusIn };
