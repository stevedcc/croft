// D3's guaranteed layer: the editor's local draft backup. On every input inside the editor's
// [data-draft-scope], the record is written to localStorage under "croft-draft:<key>" (the post
// id, or "new" before the first save) — debounced 500 ms. The four texts (title, subtitle,
// description, markdown body) are read from the DOM itself, so typing keeps being backed up even
// when the Blazor circuit is gone: a lost instance must never lose words. The other fields
// (category, tags, banner, publish date and time, navigation) come from the scope's
// data-draft-extra attribute, which the form renders from its model; a change to that attribute
// after the user acted (picking a category, adding a tag) writes the record too. A version 2
// record holds all of them; a version 1 record (title and body only) is still read. Each record
// also stores the server's saved-version stamp the editor loaded and the write time. A successful save clears the backup (blogDraft.saved,
// which moves the key to the created post's id); the editor reads the record on load
// (blogDraft.read) and the user decides — restore or discard — in the editor's own prompt.
(function () {
    const PREFIX = 'croft-draft:';

    // The debounced write: one per input burst, 500 ms after the last keystroke.
    let pending = null;
    // The editor the pending write was scheduled for: a write that fires after the user moved to
    // another editor (the script outlives in-app navigation) must not record that other editor.
    let pendingScope = null;

    // Set by the user's first event in the editor, cleared by a save. A render that rewrites
    // data-draft-extra before then is the editor loading (or reloading after a save), not an
    // edit, and must not overwrite the backup the restore prompt is about to offer.
    //
    // It belongs to one editor, not to the document: this script lives on across in-app
    // navigation, so trackScope() clears it whenever the editor's scope element is a different
    // element, or carries a different data-draft-key, than the one the flag was set for.
    let acted = false;
    let trackedScope = null;
    let trackedKey = null;

    function scope() {
        return document.querySelector('[data-draft-scope]');
    }

    function trackScope() {
        const element = scope();
        const key = element ? element.getAttribute('data-draft-key') : null;
        if (element !== trackedScope || key !== trackedKey) {
            acted = false;
            trackedScope = element;
            trackedKey = key;
        }
    }

    function keyOf(element) {
        return PREFIX + (element.getAttribute('data-draft-key') || '');
    }

    // Returns whether a record was written.
    function writeNow() {
        pending = null;
        const element = scope();
        if (!element || (pendingScope !== null && element !== pendingScope)) {
            pendingScope = null;
            return false;
        }

        pendingScope = null;

        const text = (name) => {
            const field = element.querySelector('[data-draft-field="' + name + '"]');
            const input = field && (field.matches('input, textarea') ? field : field.querySelector('input, textarea'));
            return input ? input.value : '';
        };
        let extra = {};
        try {
            extra = JSON.parse(element.getAttribute('data-draft-extra') || '{}') || {};
        } catch {
            // A malformed attribute leaves the texts, which are the part that must never be lost.
        }
        const record = {
            ...extra,
            version: 2,
            title: text('title'),
            subtitle: text('subtitle'),
            excerpt: text('excerpt'),
            body: text('body'),
            savedStamp: element.getAttribute('data-draft-stamp') || '',
            time: new Date().toISOString(),
        };
        try {
            localStorage.setItem(keyOf(element), JSON.stringify(record));
            return true;
        } catch {
            // Storage full or unavailable: the editor keeps working, the backup is best-effort —
            // the words still reach the server on save.
            return false;
        }
    }

    // The circuit calls this just before it sends the browser to sign in again (the session
    // ended): whatever the user changed is written now, not on the next change. Nothing is
    // written when the user changed nothing, so a backup waiting on disk is never overwritten
    // by an editor that has only just loaded. Returns whether a record was written.
    function flush() {
        trackScope();
        if (!acted || !scope()) {
            return false;
        }

        if (pending !== null) {
            clearTimeout(pending);
        }
        pendingScope = null;
        return writeNow();
    }

    function schedule() {
        if (pending !== null) {
            clearTimeout(pending);
        }
        pendingScope = scope();
        pending = setTimeout(writeNow, 500);
    }

    function onInput(event) {
        const element = scope();
        if (!element || !element.contains(event.target)) {
            return;
        }

        trackScope();
        // While the restore prompt waits, only typing in one of the backed-up text fields writes
        // (as it always did); the category search box and tag entry, which are not, do not.
        if (document.querySelector('.draft-restore') && !event.target.closest('[data-draft-field]')) {
            return;
        }

        acted = true;
        schedule();
    }

    // The user's own clicks and key presses arm the extra-field trigger (a category picked from
    // the list or a tag added is a click, not an input event). While the restore prompt is
    // showing they do not: the waiting backup is the user's work, and nothing may replace it
    // before the prompt is answered (typing still backs up, as it always did).
    function onAct(event) {
        const element = scope();
        if (element && element.contains(event.target)) {
            trackScope();
            if (!document.querySelector('.draft-restore')) {
                acted = true;
            }
        }
    }

    function onExtraChanged() {
        trackScope();
        if (acted && scope()) {
            schedule();
        }
    }

    function read(key) {
        try {
            const raw = localStorage.getItem(PREFIX + (key || ''));
            if (!raw) {
                return null;
            }

            const record = JSON.parse(raw);
            return {
                version: record.version || 1,
                title: record.title || '',
                body: record.body || '',
                subtitle: record.subtitle || '',
                excerpt: record.excerpt || '',
                category: record.category || null,
                tags: Array.isArray(record.tags) ? record.tags : [],
                banner: record.banner || null,
                publishedDate: record.publishedDate || null,
                publishedTime: record.publishedTime || null,
                showInNavigation: record.showInNavigation === true,
                navigationOrder: Number.isInteger(record.navigationOrder) ? record.navigationOrder : 0,
                savedStamp: record.savedStamp || '',
                time: record.time || '',
                // The prompt speaks the browser's clock (the stored value stays a precise instant).
                localTime: new Date(record.time).toLocaleString(),
            };
        } catch {
            // Storage full or unavailable (or a corrupt record): there is no backup to offer —
            // the editor keeps working without one, exactly as before this branch.
            return null;
        }
    }

    // The callers pass the post key explicitly (the editor knows it): a partially rendered DOM
    // must never mis-key a read or a delete. The DOM's own key is the write path's alone — the
    // writes must keep working with the circuit gone.
    function remove(key) {
        try {
            localStorage.removeItem(PREFIX + (key || ''));
        } catch {
            // Storage full or unavailable: the removal is best-effort — a surviving backup
            // self-heals (an identical one is deleted silently, a differing one is offered
            // again) and the editor keeps working.
        }
    }

    // The save succeeded: the backup is cleared (under "new" and the post's id alike) and every
    // later backup is keyed to the created post's id — the first save moves the key.
    function saved(newKey) {
        if (pending !== null) {
            clearTimeout(pending);
            pending = null;
        }
        try {
            localStorage.removeItem(PREFIX + 'new');
            if (newKey) {
                localStorage.removeItem(PREFIX + newKey);
            }
        } catch {
            // Storage full or unavailable: the save already succeeded and the clear is
            // best-effort — a surviving backup self-heals, the editor keeps working.
        }
        acted = false;
        const element = scope();
        if (element && newKey) {
            element.setAttribute('data-draft-key', newKey);
        }
    }

    document.addEventListener('input', onInput);
    document.addEventListener('click', onAct);
    document.addEventListener('keydown', onAct);
    new MutationObserver(onExtraChanged).observe(document.documentElement, {
        subtree: true,
        attributes: true,
        attributeFilter: ['data-draft-extra'],
    });

    // A reload, tab close or navigation must not eat the debounce window: flush the pending write.
    window.addEventListener('pagehide', () => {
        if (pending !== null) {
            writeNow();
        }
    });

    window.blogDraft = { read, remove, saved, flush };
})();
