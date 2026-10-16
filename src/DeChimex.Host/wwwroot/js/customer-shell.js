/* Customer shell behaviours: mobile search toggle, account menu, bottom sheets,
   quantity steppers, password reveal, shared light modal (gallery viewer).
   Plain JS, no dependencies. */
(function () {
  'use strict';
  var d = document;
  function $(id) { return d.getElementById(id); }
  function on(el, ev, fn) { if (el) el.addEventListener(ev, fn); }

  /* ---- mobile search ---- */
  var sToggle = $('csSearchToggle'), sForm = $('csMobileSearch');
  on(sToggle, 'click', function () {
    var open = sForm.hasAttribute('hidden');
    if (open) { sForm.removeAttribute('hidden'); var i = sForm.querySelector('input'); if (i) i.focus(); }
    else sForm.setAttribute('hidden', '');
    sToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  });

  /* ---- account menu (sheet on phones, dropdown on desktop) ---- */
  var mBtn = $('csMenuBtn'), menu = $('csMenu'), scrim = $('csScrim');
  function closeMenu() {
    if (!menu) return;
    menu.setAttribute('hidden', '');
    if (mBtn) mBtn.setAttribute('aria-expanded', 'false');
    if (scrim) scrim.classList.remove('is-open');
  }
  on(mBtn, 'click', function (e) {
    e.stopPropagation();
    var open = menu.hasAttribute('hidden');
    if (open) { menu.removeAttribute('hidden'); mBtn.setAttribute('aria-expanded', 'true'); if (scrim) scrim.classList.add('is-open'); }
    else closeMenu();
  });
  on(scrim, 'click', closeMenu);
  on(d, 'click', function (e) { if (menu && !menu.hasAttribute('hidden') && !menu.contains(e.target)) closeMenu(); });

  /* ---- generic sheets: [data-sheet-open="id"] / [data-sheet-close] ---- */
  function openSheet(id) { var s = $(id); if (!s) return; s.classList.add('is-open'); d.body.style.overflow = 'hidden'; var f = s.querySelector('button,[href],input,select'); if (f) f.focus(); }
  function closeSheets() { Array.prototype.forEach.call(d.querySelectorAll('.sh-sheet.is-open'), function (s) { s.classList.remove('is-open'); }); d.body.style.overflow = ''; }
  on(d, 'click', function (e) {
    var o = e.target.closest('[data-sheet-open]');
    if (o) { e.preventDefault(); openSheet(o.getAttribute('data-sheet-open')); return; }
    if (e.target.closest('[data-sheet-close]') || e.target.classList.contains('sh-sheet-bg')) { closeSheets(); }
  });
  on(d, 'keydown', function (e) { if (e.key === 'Escape') { closeMenu(); closeSheets(); var lb = d.querySelector('.pd-lightbox.is-open'); if (lb) lb.classList.remove('is-open'); } });

  /* ---- quantity steppers ---- */
  on(d, 'click', function (e) {
    var b = e.target.closest('[data-qty]');
    if (!b) return;
    var wrap = b.parentElement, input = wrap && wrap.querySelector('input[type=number]');
    if (!input) return;
    var v = parseInt(input.value, 10) || 1, min = parseInt(input.min, 10) || 1, max = parseInt(input.max, 10) || 10000;
    v += b.getAttribute('data-qty') === '+' ? 1 : -1;
    input.value = Math.min(max, Math.max(min, v));
    input.dispatchEvent(new Event('change', { bubbles: true }));
    if (b.hasAttribute('data-qty-submit') && !input.hasAttribute('data-submit-on-change')) { var f = input.form; if (f && f.requestSubmit) f.requestSubmit(); else if (f) f.submit(); }
  });

  /* password reveal moved to password-toggle.js (eye/eye-slash icon, owner directive 2026-10-07) */

  /* ---- auto-submit controls (sort selects, cart quantity inputs) ---- */
  on(d, 'change', function (e) {
    var s = e.target.closest('select[data-autosubmit],input[data-submit-on-change]');
    if (s && s.form) { if (s.form.requestSubmit) s.form.requestSubmit(); else s.form.submit(); }
  });

  /* ---- shared light modal (app.css .overlay/.modal) ----
     The storefront gallery viewer (Views/StoreGallery/Index.cshtml) calls
     openModal({ size, title, body }) and then drives #dcxModal itself: focus,
     carousel swaps, touch swipe, arrow/Space keys, all keyed off the element
     being present with the .open class. The helper belonged to the old
     pre-redesign storefront shell and was lost when it was replaced, leaving
     every gallery tile click a silent ReferenceError (desktop AND mobile).
     Closing: backdrop click, the × button or Escape; the body is emptied on
     close so a <video> never keeps playing behind the backdrop. */
  var modalOv = null;
  function ensureModal() {
    if (modalOv && d.body.contains(modalOv)) return modalOv;
    modalOv = d.createElement('div');
    modalOv.className = 'overlay';
    modalOv.id = 'dcxModal';
    modalOv.setAttribute('role', 'dialog');
    modalOv.setAttribute('aria-modal', 'true');
    modalOv.addEventListener('click', function (e) { if (e.target === modalOv) window.closeModal(); });
    d.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && modalOv && modalOv.classList.contains('open')) window.closeModal();
    });
    d.body.appendChild(modalOv);
    return modalOv;
  }
  window.openModal = function (opts) {
    opts = opts || {};
    var ov = ensureModal();
    var modal = d.createElement('div');
    modal.className = 'modal' + (opts.size ? ' ' + opts.size : '');
    var head = '';
    if (opts.title) {
      head = '<div class="modal-head"><h3 style="margin:0;font-size:16px;font-weight:700">' + opts.title + '</h3>'
        + '<button type="button" class="btn btn-outline btn-sm" data-modal-close aria-label="Close">&times;</button></div>';
    }
    modal.innerHTML = head
      + '<div class="modal-body">' + (opts.body || '') + '</div>'
      + (opts.foot ? '<div class="modal-foot">' + opts.foot + '</div>' : '');
    ov.innerHTML = '';
    ov.appendChild(modal);
    ov.classList.add('open');
    var closeBtn = modal.querySelector('[data-modal-close]');
    if (closeBtn) closeBtn.addEventListener('click', window.closeModal);
    return modal;
  };
  window.closeModal = function () {
    if (!modalOv) return;
    modalOv.classList.remove('open');
    window.setTimeout(function () {
      if (modalOv && !modalOv.classList.contains('open')) modalOv.innerHTML = '';
    }, 220);
  };
})();
