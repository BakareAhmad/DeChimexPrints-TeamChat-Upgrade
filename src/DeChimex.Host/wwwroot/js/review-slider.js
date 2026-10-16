/* Home page review slider (owner directive 2026-10-07).
 *
 * Progressive enhancement only: the track is a native scroll-snap container, so touch swipe
 * and kinetic scrolling already work without this file. What this adds is the desktop affordances
 * (arrows, dots, mouse drag), autoplay with polite pausing, and keyboard control.
 *
 * Markup contract (Views/StoreHome/Index.cshtml):
 *   [data-review-slider] > [data-review-track] > .hm-review-slide
 *   optional [data-review-prev] [data-review-next] [data-review-dots]
 */
(function () {
  'use strict';

  var AUTOPLAY_MS = 6000;
  var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

  function initSlider(root) {
    var track = root.querySelector('[data-review-track]');
    if (!track) return;

    var slides = Array.prototype.slice.call(track.querySelectorAll('.hm-review-slide'));
    if (slides.length < 2) return; // nothing to slide

    var prev = root.querySelector('[data-review-prev]');
    var next = root.querySelector('[data-review-next]');
    var dotsHost = root.querySelector('[data-review-dots]');
    var dots = [];
    var pages = 1;
    var current = 0;
    var timer = null;
    var paused = false;

    // How many slides fit side by side right now (1 on phones, 2 on tablets, 3 on desktop).
    function perView() {
      var slide = slides[0];
      var gap = parseFloat(window.getComputedStyle(track).columnGap || '0') || 0;
      var width = slide.getBoundingClientRect().width + gap;
      return width > 0 ? Math.max(1, Math.round(track.clientWidth / width)) : 1;
    }

    function pageCount() {
      return Math.max(1, slides.length - perView() + 1);
    }

    function buildDots() {
      if (!dotsHost) return;
      dotsHost.innerHTML = '';
      dots = [];
      for (var i = 0; i < pages; i++) {
        var b = document.createElement('button');
        b.type = 'button';
        b.className = 'hm-review-dot';
        b.setAttribute('role', 'tab');
        b.setAttribute('aria-label', 'Review ' + (i + 1));
        b.dataset.index = String(i);
        b.addEventListener('click', function () {
          goTo(parseInt(this.dataset.index, 10));
          restart();
        });
        dotsHost.appendChild(b);
        dots.push(b);
      }
    }

    function offsetFor(index) {
      var slide = slides[Math.min(index, slides.length - 1)];
      return slide.offsetLeft - slides[0].offsetLeft;
    }

    function goTo(index, smooth) {
      pages = pageCount();
      if (index < 0) index = pages - 1;          // wrap around
      if (index > pages - 1) index = 0;
      current = index;
      track.scrollTo({ left: offsetFor(index), behavior: smooth === false ? 'auto' : 'smooth' });
      paint();
    }

    function paint() {
      var atStart = current <= 0;
      var atEnd = current >= pages - 1;
      if (prev) prev.disabled = pages <= 1;
      if (next) next.disabled = pages <= 1;
      for (var i = 0; i < dots.length; i++) {
        var on = i === current;
        dots[i].setAttribute('aria-selected', on ? 'true' : 'false');
        dots[i].setAttribute('tabindex', on ? '0' : '-1');
      }
      // Slides outside the viewport are hidden from assistive tech.
      for (var s = 0; s < slides.length; s++) {
        var visible = s >= current && s < current + perView();
        slides[s].setAttribute('aria-hidden', visible ? 'false' : 'true');
      }
      void atStart; void atEnd;
    }

    function nearestIndex() {
      var left = track.scrollLeft + slides[0].offsetLeft;
      var best = 0;
      var bestDelta = Infinity;
      for (var i = 0; i < slides.length; i++) {
        var delta = Math.abs(slides[i].offsetLeft - left);
        if (delta < bestDelta) { bestDelta = delta; best = i; }
      }
      return Math.min(best, pages - 1);
    }

    // --- autoplay (pauses on hover, focus, touch and when the tab is hidden) ---
    function tick() {
      if (paused || document.hidden) return;
      goTo(current + 1);
    }
    function start() {
      if (reduceMotion || timer) return;
      timer = window.setInterval(tick, AUTOPLAY_MS);
    }
    function stop() {
      if (timer) { window.clearInterval(timer); timer = null; }
    }
    function restart() { stop(); start(); }

    ['mouseenter', 'focusin', 'touchstart', 'pointerdown'].forEach(function (evt) {
      root.addEventListener(evt, function () { paused = true; stop(); }, { passive: true });
    });
    ['mouseleave', 'focusout'].forEach(function (evt) {
      root.addEventListener(evt, function () { paused = false; start(); }, { passive: true });
    });
    root.addEventListener('touchend', function () {
      window.setTimeout(function () { paused = false; start(); }, 1200);
    }, { passive: true });
    document.addEventListener('visibilitychange', function () {
      if (document.hidden) stop(); else start();
    });

    // --- controls ---
    if (prev) prev.addEventListener('click', function () { goTo(current - 1); restart(); });
    if (next) next.addEventListener('click', function () { goTo(current + 1); restart(); });

    track.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowLeft') { e.preventDefault(); goTo(current - 1); restart(); }
      else if (e.key === 'ArrowRight') { e.preventDefault(); goTo(current + 1); restart(); }
    });

    // Keep dots/buttons in sync when the user swipes or scrolls by hand.
    var scrollRaf = null;
    track.addEventListener('scroll', function () {
      if (scrollRaf) return;
      scrollRaf = window.requestAnimationFrame(function () {
        scrollRaf = null;
        var idx = nearestIndex();
        if (idx !== current) { current = idx; paint(); }
      });
    }, { passive: true });

    // --- desktop mouse drag (touch already scrolls natively) ---
    var dragging = false;
    var startX = 0;
    var startLeft = 0;
    var moved = 0;
    track.addEventListener('mousedown', function (e) {
      dragging = true; moved = 0;
      startX = e.pageX; startLeft = track.scrollLeft;
      track.style.cursor = 'grabbing';
      track.style.scrollSnapType = 'none';
    });
    window.addEventListener('mousemove', function (e) {
      if (!dragging) return;
      var dx = e.pageX - startX;
      moved = Math.abs(dx);
      track.scrollLeft = startLeft - dx;
    });
    window.addEventListener('mouseup', function () {
      if (!dragging) return;
      dragging = false;
      track.style.cursor = '';
      track.style.scrollSnapType = '';
      if (moved > 8) goTo(nearestIndex(), false);
    });
    track.addEventListener('click', function (e) {
      if (moved > 8) { e.preventDefault(); e.stopPropagation(); moved = 0; }
    }, true);

    // Rebuild paging when the layout changes (rotation, resize, font load).
    var resizeRaf = null;
    window.addEventListener('resize', function () {
      if (resizeRaf) return;
      resizeRaf = window.requestAnimationFrame(function () {
        resizeRaf = null;
        var newPages = pageCount();
        if (newPages !== pages) {
          pages = newPages;
          buildDots();
          goTo(Math.min(current, pages - 1), false);
        } else {
          paint();
        }
      });
    });

    pages = pageCount();
    buildDots();
    paint();
    start();
  }

  function init() {
    var sliders = document.querySelectorAll('[data-review-slider]');
    for (var i = 0; i < sliders.length; i++) initSlider(sliders[i]);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
