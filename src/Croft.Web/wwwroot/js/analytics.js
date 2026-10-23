// Records post views with a fire-and-forget beacon so the browser's address and
// document.referrer reach the analytics endpoint instead of the Web pod's address.
window.blogAnalytics = {
    recordPageView: function (postId) {
        try {
            if (!navigator.sendBeacon) {
                return;
            }
            // WORKAROUND(W-044, Browsers): navigator.sendBeacon posts the JSON as text/plain, so the server parses the raw body
            navigator.sendBeacon(
                "/api/analytics/pageview",
                JSON.stringify({ postId: postId, referrer: document.referrer }));
            // END WORKAROUND(W-044)
        } catch (e) {
            // Analytics must never break the page.
        }
    }
};
