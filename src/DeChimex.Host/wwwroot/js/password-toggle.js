/* Eye / eye-slash visibility toggle for password fields (owner directive 2026-10-07).
 * Binds to every [data-pw-toggle] button (rendered by Views/Shared/_PwToggle.cshtml).
 * The button flips its paired input between type=password and type=text and swaps the
 * eye / eye-slash icon via .is-shown, with aria-pressed + aria-label mirroring the
 * state. Purely visual: it never touches validation, autocomplete or form submission.
 */
(function () {
    "use strict";
    document.addEventListener("click", function (e) {
        var b = e.target.closest("[data-pw-toggle]");
        if (!b) return;
        var wrap = b.parentElement;
        var i = wrap && wrap.querySelector("input");
        if (!i) return;
        var show = i.type === "password";
        i.type = show ? "text" : "password";
        b.classList.toggle("is-shown", show);
        b.setAttribute("aria-pressed", show ? "true" : "false");
        b.setAttribute("aria-label", show ? "Hide password" : "Show password");
    });
})();
