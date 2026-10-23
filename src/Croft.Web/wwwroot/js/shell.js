// Layout shell behaviour: ☰ toggles, sidebar Collapse, Esc/scrim close, focus trap,
// blog-sidebar persistence and theme cycling. Without JavaScript every width still renders its
// default state from CSS alone (spec §4.1/§4.3); this module only adds the interactions.

const SIDEBAR_KEY = "blog-sidebar";
const THEME_KEY = "blog-theme";

let initialized = false;
let lastNavToggle = null;

// markInteractive: whether init() itself marks the page as ready for interaction. MainLayout's
// OnAfterRenderAsync calls it with the default (it runs on the interactive render only, so DOM
// events dispatched before it have no Blazor handlers and are silently dropped — end-to-end tests
// wait for the mark before interacting). The static reading pages pass false: static-glue.js sets
// the mark itself once the page's own JavaScript is ready.
export function init(markInteractive = true) {
    // WORKAROUND(W-011, Blazor): DOM events dispatched before the first interactive render are dropped; data-interactive marks when they work
    if (markInteractive) {
        document.documentElement.dataset.interactive = "on";
    }
    // END WORKAROUND(W-011)

    if (initialized) return;
    initialized = true;

    // Delegated listeners: they survive Blazor re-renders of the sidebar's children.
    document.addEventListener("click", (event) => {
        const toggle = event.target.closest("[data-nav-toggle]");
        if (toggle) {
            toggleNav(toggle);
            return;
        }
        if (event.target.closest("[data-sidebar-collapse]")) {
            toggleSidebar();
            return;
        }
        if (event.target.closest(".scrim")) {
            closeNav();
            return;
        }
        // The overlay closes on navigation.
        if (event.target.closest("#sidebar-nav a")) {
            closeNav();
        }
    });

    document.addEventListener("keydown", (event) => {
        if (!document.documentElement.classList.contains("nav-open")) return;
        if (event.key === "Escape") {
            closeNav();
            return;
        }
        if (event.key === "Tab") {
            trapFocus(event, document.getElementById("sidebar-nav"));
        }
    });

    // WORKAROUND(W-003, Browsers): type=search inputs clear themselves on Escape; the default is stopped so a typed query survives
    // A typed search query must survive Escape: type=search inputs clear themselves on Escape,
    // which the field never did — the key's default is stopped for search inputs only.
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && event.target instanceof HTMLInputElement && event.target.type === "search") {
            event.preventDefault();
        }
    });
    // END WORKAROUND(W-003)
}

// Puts the saved theme and sidebar state back on the <html> element, the way the inline theme
// bootstrap in the document head does on a full load. An enhanced navigation patches <html>
// from the server's markup, which knows neither (they are set by script), so the static reading
// pages call this after every one.
// WORKAROUND(W-004, Blazor): enhanced navigation resets <html> attributes and classes to the server's copy
export function restoreSavedState() {
    const mode = currentMode();
    if (mode === "system") {
        document.documentElement.removeAttribute("data-theme");
    } else {
        document.documentElement.setAttribute("data-theme", mode);
    }

    let collapsed = false;
    try {
        collapsed = localStorage.getItem(SIDEBAR_KEY) === "collapsed";
    } catch {
        // Storage blocked: the default (expanded) applies.
    }
    document.documentElement.classList.toggle("sidebar-collapsed", collapsed);
}
// END WORKAROUND(W-004)

export function toggleNav(toggle) {
    if (document.documentElement.classList.contains("nav-open")) {
        closeNav();
    } else {
        openNav(toggle);
    }
}

export function openNav(toggle) {
    lastNavToggle = toggle || document.querySelector("[data-nav-toggle]");
    document.documentElement.classList.add("nav-open");
    syncNavToggles(true);

    const sidebar = document.getElementById("sidebar-nav");
    // Only rendered elements can take focus: the first candidate in DOM order is the rail toggle,
    // which is display:none below 960px.
    const first = sidebar && Array.from(sidebar.querySelectorAll("a[href], button"))
        .find((element) => element.getClientRects().length > 0);
    if (first) first.focus();
}

export function closeNav() {
    const wasOpen = document.documentElement.classList.contains("nav-open");
    document.documentElement.classList.remove("nav-open");
    syncNavToggles(false);

    if (wasOpen && lastNavToggle && lastNavToggle.getClientRects().length > 0) {
        lastNavToggle.focus();
    }
}

export function toggleSidebar() {
    const collapsed = document.documentElement.classList.toggle("sidebar-collapsed");
    try {
        if (collapsed) {
            localStorage.setItem(SIDEBAR_KEY, "collapsed");
        } else {
            localStorage.removeItem(SIDEBAR_KEY);
        }
    } catch {
        // Storage blocked: the class still applies for the current page.
    }
}

export function currentMode() {
    try {
        const stored = localStorage.getItem(THEME_KEY);
        return stored === "light" || stored === "dark" ? stored : "system";
    } catch {
        return "system";
    }
}

export function cycleTheme() {
    const current = currentMode();
    const next = current === "system" ? "light" : current === "light" ? "dark" : "system";
    applyMode(next);
    return next;
}

export function isDark() {
    const explicit = document.documentElement.getAttribute("data-theme");
    if (explicit === "dark") return true;
    if (explicit === "light") return false;
    return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function applyMode(mode) {
    try {
        if (mode === "system") {
            localStorage.removeItem(THEME_KEY);
        } else {
            localStorage.setItem(THEME_KEY, mode);
        }
    } catch {
        // Storage blocked: the attribute still applies for the current page.
    }

    if (mode === "system") {
        document.documentElement.removeAttribute("data-theme");
    } else {
        document.documentElement.setAttribute("data-theme", mode);
    }
}

function syncNavToggles(expanded) {
    document.querySelectorAll("[data-nav-toggle]").forEach((button) => {
        button.setAttribute("aria-expanded", expanded ? "true" : "false");
    });
}

function trapFocus(event, container) {
    if (!container) return;

    const focusable = Array.from(
        container.querySelectorAll('a[href], button:not([disabled]), input, select, textarea, [tabindex]:not([tabindex="-1"])')
    ).filter((element) => element.getClientRects().length > 0);
    if (focusable.length === 0) return;

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;

    if (event.shiftKey) {
        if (active === first || !container.contains(active)) {
            event.preventDefault();
            last.focus();
        }
    } else if (active === last || !container.contains(active)) {
        event.preventDefault();
        first.focus();
    }
}
