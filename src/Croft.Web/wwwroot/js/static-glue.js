// Starts the reading pages' JavaScript without a Blazor circuit. The reading pages are static SSR
// (App.razor marks the document data-render="static"), so nothing runs their OnAfterRenderAsync:
// shell.js and post.js, which interactive pages start over JS interop, are started here — on
// the first load and after every enhanced navigation (Blazor's `enhancedload`), because an
// enhanced navigation swaps the page's DOM without loading a new document. Every start is
// idempotent or tears the previous one down first, so repeated navigations never stack listeners.
// The theme switch is a Blazor @onclick on interactive pages; on static pages this module
// handles its click instead. On interactive pages (admin, editor) the module does nothing.
import * as shell from "./shell.js";

const root = document.documentElement;
const labels = { light: "Light", dark: "Dark", system: "System" };

if (root.dataset.render === "static") {
    // The theme switch: one delegated listener for the document's lifetime.
    document.addEventListener("click", (event) => {
        if (event.target.closest(".theme-switch")) {
            showThemeMode(shell.cycleTheme() ?? shell.currentMode());
        }
    });

    // WORKAROUND(W-087, Blazor): an enhanced navigation swaps the DOM without loading a new document, so the page scripts are started again on enhancedload
    window.Blazor?.addEventListener("enhancedload", boot);
    // END WORKAROUND(W-087)
    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", boot);
    } else {
        boot();
    }
}

// The page's JavaScript is ready once this has run: data-interactive="on" is the signal the
// end-to-end tests wait for (shell.init marks it itself only on interactive pages). It is
// cleared first so that after an enhanced navigation it means this page, not the previous one.
async function boot() {
    delete root.dataset.interactive;
    try {
        shell.init(false);
        // WORKAROUND(W-004, Blazor): enhanced navigation patches <html> and the theme switch back to the server's copy: restore the saved state and re-sync the switch
        shell.restoreSavedState();
        showThemeMode(shell.currentMode());
        // END WORKAROUND(W-004)
        recordPostView();

        // WORKAROUND(W-085, Blazor): enhanced navigation disposes no component, so the previous post's scroll listeners stay until torn down here, before anything of the new page can fail
        window.blogPost?.teardownPost();
        // END WORKAROUND(W-085)

        if (document.querySelector(".markdown-body")) {
            await import("./post.js");
            const post = window.blogPost;
            post.highlightAll();
            post.decorateImages();
            post.scrollToHash();
            if (document.querySelector(".toc__link")) {
                post.initScrollSpy();
                post.initContentsPanel();
            }
        }
    } catch (error) {
        // A script that cannot load (blocked, stale after a deploy, a network error) or one that throws
        // must not leave the page half started with the cause unobserved: it is logged, and the page
        // still counts as started, so the shell the first part of boot() set up stays usable.
        console.error("static-glue: starting the page's scripts failed", error);
    } finally {
        root.dataset.interactive = "on";
    }
}

// The page-view beacon: Post.razor renders the post's id as data-post-id on its layout, and each
// boot is one view — the first load or one enhanced navigation — so a post page sends exactly one
// beacon per view and every other page none.
function recordPostView() {
    const postId = document.querySelector("[data-post-id]")?.dataset.postId;
    if (postId) {
        window.blogAnalytics?.recordPageView(postId);
    }
}

// WORKAROUND(W-086, Blazor): a static page has no circuit to re-render the theme switch, so script writes its data-mode, title and label
// The switch's state as the Blazor component renders it: the data-mode that picks the icon, the
// title and the label. The server always renders "System"; the stored preference is shown here.
function showThemeMode(mode) {
    const text = `Theme: ${labels[mode] ?? labels.system}`;
    for (const button of document.querySelectorAll(".theme-switch")) {
        button.dataset.mode = mode;
        button.title = text;
        const label = button.querySelector(".label");
        if (label) label.textContent = text;
    }
}
// END WORKAROUND(W-086)
