// Smart back navigation (nav audit 2026-10-09).
// Links marked [data-smart-back] prefer the browser history so list state
// (filters, search, pagination, tabs, scroll) is preserved, and fall back to
// their href when there is no safe in-app history: direct URL entry, refresh,
// POST-redirect chains, external referrers or a same-URL referrer.
(function () {
    'use strict';

    function sameOrigin(url) {
        try { return new URL(url, window.location.origin).origin === window.location.origin; }
        catch (e) { return false; }
    }

    document.addEventListener('click', function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        var a = e.target.closest ? e.target.closest('a[data-smart-back]') : null;
        if (!a) return;
        var ref = document.referrer;
        if (!ref || !sameOrigin(ref)) return;                 // direct entry / external: follow href
        if (!(window.history.length > 1)) return;            // no history: follow href
        var r = new URL(ref);
        if (r.pathname + r.search === window.location.pathname + window.location.search) return; // refresh or POST-redirect: follow href
        e.preventDefault();
        window.history.back();
    }, false);
})();
