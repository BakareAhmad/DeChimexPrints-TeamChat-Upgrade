/* Service announcement pill (owner directive 2026-10-07).
 *
 * Progressive enhancement for the .cs-announce ticker in _CustomerLayout: when the
 * announcement text fits the viewport it stays static; when it overflows, the single
 * message group is cloned once and the track scrolls seamlessly (translateX -50%),
 * pausing on hover/focus. Respects prefers-reduced-motion (static, scrollable).
 * Touch devices need nothing here: the strip is a normal static element when the
 * text fits, and the marquee is a transform animation (not native scroll), so it
 * never fights vertical page swipes.
 */
(function () {
    "use strict";

    var el = document.querySelector("[data-announce-ticker]");
    if (!el) return;
    var view = el.querySelector(".cs-announce-view");
    var track = el.querySelector(".cs-announce-track");
    var group = track && track.querySelector(".cs-announce-group");
    if (!view || !track || !group) return;

    var reduce = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    var clone = null;

    function reset() {
        track.classList.remove("is-anim");
        track.style.removeProperty("--announce-dur");
        if (clone) { clone.remove(); clone = null; }
    }

    function setup() {
        reset();
        var viewW = view.clientWidth;
        var contentW = group.offsetWidth; // one group incl. its trailing gap
        if (reduce || contentW <= viewW) return; // fits: leave it static
        clone = group.cloneNode(true);
        clone.setAttribute("aria-hidden", "true");
        track.appendChild(clone);
        var dur = Math.max(12, Math.round(contentW / 45)); // ~45 px per second
        track.style.setProperty("--announce-dur", dur + "s");
        track.classList.add("is-anim");
    }

    var t;
    window.addEventListener("resize", function () { clearTimeout(t); t = setTimeout(setup, 200); });
    if (document.readyState === "complete") setup();
    else window.addEventListener("load", setup);
})();
