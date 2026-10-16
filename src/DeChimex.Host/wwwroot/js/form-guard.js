// Unsaved-changes guard (nav audit 2026-10-09).
// Forms opted in with [data-guard] are tracked: any input/change marks them
// dirty; submitting clears the flag. While dirty, leaving the page through an
// in-app link asks for confirmation, and browser-level unload is intercepted.
// Search/filter forms, chat boxes and quick actions stay unguarded (opt-in).
(function () {
    'use strict';

    var MESSAGE = 'You have unsaved changes on this page. Leave and discard them?';

    function mark(form, dirty) {
        if (dirty) { form.setAttribute('data-dirty', '1'); }
        else { form.removeAttribute('data-dirty'); }
    }

    function dirtyForm() {
        return document.querySelector('form[data-guard][data-dirty]');
    }

    document.addEventListener('input', function (e) {
        var f = e.target && e.target.closest ? e.target.closest('form[data-guard]') : null;
        if (f) mark(f, true);
    }, true);

    document.addEventListener('change', function (e) {
        var f = e.target && e.target.closest ? e.target.closest('form[data-guard]') : null;
        if (f) mark(f, true);
    }, true);

    // Intentional save/submit: never block the submission itself.
    document.addEventListener('submit', function (e) {
        var f = e.target && e.target.closest ? e.target.closest('form[data-guard]') : null;
        if (f) mark(f, false);
    }, true);

    // In-app links: confirm before discarding work. Links inside the dirty form
    // are exempt (cancel-style controls live with the form); so are #anchors,
    // download links, javascript: and explicit [data-guard-exempt] escapes.
    document.addEventListener('click', function (e) {
        if (e.defaultPrevented) return;
        var a = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!a) return;
        if (a.hasAttribute('data-guard-exempt')) return;
        var href = a.getAttribute('href') || '';
        if (href.charAt(0) === '#' || href.indexOf('javascript:') === 0 || a.hasAttribute('download')) return;
        var f = dirtyForm();
        if (!f) return;
        if (f.contains(a)) return;
        if (!window.confirm(MESSAGE)) {
            e.preventDefault();
            e.stopImmediatePropagation();
            return;
        }
        mark(f, false);
    }, true);

    window.addEventListener('beforeunload', function (e) {
        if (dirtyForm()) {
            e.preventDefault();
            e.returnValue = '';
        }
    });
})();
