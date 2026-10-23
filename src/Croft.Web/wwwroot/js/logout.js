// The sign-out interstitial's script: a shared computer keeps no drafts after sign-out, so every
// local draft backup goes before the sign-out continues: the interstitial's form (POST
// /logout/complete with an antiforgery token: the cookie and IdP sign-out) is submitted. Without
// script the form's visible button continues by hand.
(function () {
    try {
        for (let i = localStorage.length - 1; i >= 0; i--) {
            const key = localStorage.key(i);
            if (key && key.startsWith('croft-draft:')) {
                localStorage.removeItem(key);
            }
        }
    } catch {
        // Storage full or unavailable: the clearing is best-effort — the sign-out must still
        // complete (the form's visible button is the no-script fallback).
    }
    document.getElementById('logout-form')?.requestSubmit();
})();
