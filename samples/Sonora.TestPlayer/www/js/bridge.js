/*
 * bridge.js — Vetra bridge access plus small shared helpers (events bus, formatting, safe DOM builders, toasts).
 *
 * Security rule for the whole UI: anything that originates outside this script (file paths, URLs, error and event
 * text, tag metadata) is placed into the DOM with textContent / Text nodes only. There is no innerHTML anywhere.
 */
(function () {
  'use strict';

  const TP = (window.TP = window.TP || {});

  // ---------------------------------------------------------------------------------------------
  // Shared state (single source of truth for the UI; updated by app.js from bridge events)
  // ---------------------------------------------------------------------------------------------
  TP.state = {
    appInfo: null,
    capabilities: null,
    dialogsEnabled: false,
    /** User Music folder path from the host; used as the default open location for add/load dialogs. */
    musicFolder: null,
    snapshot: null,
    queue: [],
    currentItem: null,
    devices: [],
    output: null,
    dsp: null,
    streamStatus: null,
    resources: null,
    soak: null,
    section: 'player',
  };

  /** Bridge event names exactly as pushed by the host. */
  TP.EVENTS = Object.freeze({
    snapshot: 'engine.snapshot',
    position: 'playback.position',
    trackChanged: 'playback.trackChanged',
    completed: 'playback.completed',
    queueChanged: 'queue.changed',
    diagnosticEvent: 'diagnostics.event',
    gaplessPcmPhase: 'diagnostics.gaplessPcm.phase',
    listeningAbPhase: 'diagnostics.listeningAb.phase',
    listeningAbAwaitObservation: 'diagnostics.listeningAb.awaitObservation',
    listeningAbObservationReceived: 'diagnostics.listeningAb.observationReceived',
    stall: 'diagnostics.stall',
    resources: 'diagnostics.resources',
    devicesChanged: 'devices.changed',
    outputChanged: 'output.changed',
    networkChanged: 'network.changed',
    error: 'playback.error',
  });

  // ---------------------------------------------------------------------------------------------
  // Internal pub/sub (decouples modules from vetra.on)
  // ---------------------------------------------------------------------------------------------
  const listeners = new Map();
  TP.bus = {
    on(name, handler) {
      let set = listeners.get(name);
      if (!set) {
        set = new Set();
        listeners.set(name, set);
      }
      set.add(handler);
      return () => set.delete(handler);
    },
    emit(name, payload) {
      const set = listeners.get(name);
      if (!set) return;
      for (const handler of Array.from(set)) {
        try {
          handler(payload);
        } catch (err) {
          console.error(`Handler for '${name}' failed:`, err);
        }
      }
    },
  };

  // ---------------------------------------------------------------------------------------------
  // Vetra access
  // ---------------------------------------------------------------------------------------------
  async function whenVetra() {
    for (;;) {
      if (window.vetra) return window.vetra;
      await new Promise((r) => setTimeout(r, 50));
    }
  }

  /** Raw invoke: positional arguments are sent as the JSON args array. Rejects with the Vetra error. */
  async function invoke(name, ...args) {
    const vetra = await whenVetra();
    return vetra.invoke(name, ...args);
  }

  /** Readable text for a Vetra error (message plus code when it adds information). */
  function errorText(err) {
    if (!err) return 'Unknown error';
    const message = typeof err === 'string' ? err : err.message || String(err);
    const code = err.code;
    return code && code !== 'command_failed' && !message.includes(code) ? `${message} [${code}]` : message;
  }

  /**
   * Invokes a command and never throws. Returns { ok, value } or { ok:false, error, message }.
   * Errors are shown as a toast unless opts.toast === false. opts.label prefixes the toast.
   */
  async function run(name, args = [], opts = {}) {
    try {
      const value = await invoke(name, ...args);
      return { ok: true, value };
    } catch (error) {
      const message = errorText(error);
      if (opts.toast !== false) {
        TP.ui.toast(opts.label ? `${opts.label}: ${message}` : message, 'danger');
      }
      return { ok: false, error, message };
    }
  }

  /** Subscribes to a host event once vetra exists. Returns an unsubscribe function. */
  function on(eventName, handler) {
    let cancelled = false;
    let unsubscribe = null;
    whenVetra().then((vetra) => {
      if (!cancelled) unsubscribe = vetra.on(eventName, handler);
    });
    return () => {
      cancelled = true;
      if (unsubscribe) unsubscribe();
    };
  }

  async function dialog(method, options) {
    try {
      const vetra = await whenVetra();
      const value = await vetra.dialogs[method](options);
      return { ok: true, value };
    } catch (error) {
      TP.ui.toast(errorText(error), 'danger');
      return { ok: false, error, message: errorText(error) };
    }
  }

  const AUDIO_FILTERS = [
    { name: 'Audio files', extensions: ['mp3', 'wav', 'flac', 'ogg', 'm4a', 'aac', 'wma', 'aiff', 'opus'] },
    { name: 'All files', extensions: ['*'] },
  ];

  TP.bridge = {
    whenVetra,
    invoke,
    run,
    on,
    errorText,
    AUDIO_FILTERS,
    dialogs: {
      /** Resolves { ok, value: string[] } (empty array when cancelled). */
      openFiles: (options) => dialog('openFiles', options),
      /** Resolves { ok, value: string|null }. */
      openFile: (options) => dialog('openFile', options),
      /** Resolves { ok, value: string|null }. */
      saveFile: (options) => dialog('saveFile', options),
    },
  };

  // ---------------------------------------------------------------------------------------------
  // Formatting
  // ---------------------------------------------------------------------------------------------
  const UNKNOWN_TIME = '—:—';

  /** m:ss, or h:mm:ss from one hour up. Unknown / invalid input yields "—:—". Input is milliseconds. */
  function formatTime(ms) {
    if (typeof ms !== 'number' || !Number.isFinite(ms) || ms < 0) return UNKNOWN_TIME;
    const total = Math.floor(ms / 1000);
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    const ss = String(s).padStart(2, '0');
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
  }

  /** Duration respecting the engine's hasKnownDuration flag. */
  function formatDuration(ms, known) {
    return known ? formatTime(ms) : UNKNOWN_TIME;
  }

  /** "preferredUnavailable" -> "Preferred unavailable". */
  function humanize(value) {
    if (value == null || value === '') return '—';
    const spaced = String(value).replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/_/g, ' ').toLowerCase();
    return spaced.charAt(0).toUpperCase() + spaced.slice(1);
  }

  function formatBytes(bytes) {
    if (typeof bytes !== 'number' || !Number.isFinite(bytes)) return '—';
    const units = ['B', 'KB', 'MB', 'GB'];
    let value = Math.abs(bytes);
    let i = 0;
    while (value >= 1024 && i < units.length - 1) {
      value /= 1024;
      i += 1;
    }
    const sign = bytes < 0 ? '−' : '';
    return `${sign}${value.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
  }

  function formatSignedBytes(bytes) {
    if (typeof bytes !== 'number' || !Number.isFinite(bytes)) return '—';
    return `${bytes > 0 ? '+' : ''}${formatBytes(bytes)}`;
  }

  function formatDb(value, digits = 1) {
    if (typeof value !== 'number' || !Number.isFinite(value)) return '—';
    const fixed = value.toFixed(digits);
    return `${value > 0 ? '+' : ''}${fixed === '-0.0' ? '0.0' : fixed}`;
  }

  function formatLocalTime(iso, withMs = false) {
    if (!iso) return '—';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '—';
    const pad = (n, w = 2) => String(n).padStart(w, '0');
    const base = `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
    return withMs ? `${base}.${pad(d.getMilliseconds(), 3)}` : base;
  }

  function formatElapsedSeconds(seconds) {
    if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0) return '—';
    return formatTime(seconds * 1000);
  }

  /** Last path segment of a file path; for URLs, host and path without query or fragment. */
  function displayName(source) {
    if (!source) return '—';
    const text = String(source);
    if (/^https?:\/\//i.test(text)) {
      try {
        const url = new URL(text);
        return url.host + url.pathname;
      } catch {
        return text.split(/[?#]/)[0];
      }
    }
    const parts = text.split(/[\\/]/);
    return parts[parts.length - 1] || text;
  }

  TP.fmt = {
    UNKNOWN_TIME,
    time: formatTime,
    duration: formatDuration,
    humanize,
    bytes: formatBytes,
    signedBytes: formatSignedBytes,
    db: formatDb,
    localTime: formatLocalTime,
    elapsedSeconds: formatElapsedSeconds,
    name: displayName,
  };

  // ---------------------------------------------------------------------------------------------
  // DOM helpers (text only)
  // ---------------------------------------------------------------------------------------------
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => Array.from(root.querySelectorAll(selector));

  /**
   * Creates an element. attrs: class, text, dataset, style (object of CSS properties), on<event> functions;
   * everything else becomes an attribute. Children are strings (as text nodes) or nodes. There is no html option.
   */
  function el(tag, attrs, ...children) {
    const node = document.createElement(tag);
    if (attrs) {
      for (const [key, value] of Object.entries(attrs)) {
        if (value == null || value === false) continue;
        if (key === 'class') node.className = value;
        else if (key === 'text') node.textContent = value;
        else if (key === 'dataset') Object.assign(node.dataset, value);
        else if (key === 'style' && typeof value === 'object') {
          for (const [prop, v] of Object.entries(value)) node.style.setProperty(prop, v);
        } else if (key.startsWith('on')) {
          if (typeof value === 'function') node.addEventListener(key.slice(2).toLowerCase(), value);
        } else node.setAttribute(key, value === true ? '' : String(value));
      }
    }
    for (const child of children.flat()) {
      if (child == null || child === false) continue;
      node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
    return node;
  }

  function setText(node, text) {
    if (node && node.textContent !== text) node.textContent = text;
  }

  /** Fills a pill element: text plus a tone (ok / warn / bad / info / accent / muted). */
  function setPill(node, text, tone) {
    if (!node) return;
    node.classList.add('tp-pill');
    setText(node, text);
    node.dataset.tone = tone || 'muted';
  }

  /** A new pill span. */
  function pill(text, tone) {
    const node = el('span', { class: 'tp-pill' });
    setPill(node, text, tone);
    return node;
  }

  /** Paints a range input's fill colour (0–100%). */
  function setRangeFill(input) {
    const min = Number(input.min || 0);
    const max = Number(input.max || 100);
    const span = max - min;
    const pct = span > 0 ? ((Number(input.value) - min) / span) * 100 : 0;
    input.style.setProperty('--fill', `${Math.max(0, Math.min(100, pct))}%`);
  }

  /**
   * Serialises rapid-fire sends: only the newest value is sent once the previous send completes, so a slow
   * response can never overwrite a newer value. `send(value)` must handle its own errors.
   */
  function latestWins(send) {
    let inFlight = false;
    let hasPending = false;
    let pending;
    async function pump() {
      if (inFlight) return;
      inFlight = true;
      try {
        while (hasPending) {
          const value = pending;
          hasPending = false;
          try {
            await send(value);
          } catch (err) {
            console.error('latestWins send failed:', err);
          }
        }
      } finally {
        inFlight = false;
        // Restart if a value arrived between the last while-check and clearing inFlight.
        if (hasPending) queueMicrotask(pump);
      }
    }
    return (value) => {
      pending = value;
      hasPending = true;
      pump();
    };
  }

  // ---------------------------------------------------------------------------------------------
  // Toasts
  // ---------------------------------------------------------------------------------------------
  const TOAST_ICONS = {
    danger: 'bi-x-octagon-fill',
    warning: 'bi-exclamation-triangle-fill',
    success: 'bi-check-circle-fill',
    info: 'bi-info-circle-fill',
  };
  const MAX_TOASTS = 5;

  /** Shows a toast. kind: danger | warning | success | info. `message` is always rendered as text. */
  function toast(message, kind = 'info') {
    const host = document.getElementById('toast-host');
    if (!host) return;
    const tone = TOAST_ICONS[kind] ? kind : 'info';

    while (host.children.length >= MAX_TOASTS) host.firstElementChild.remove();

    const node = el(
      'div',
      { class: `toast tp-toast tp-toast-${tone}`, role: tone === 'danger' ? 'alert' : 'status', 'aria-live': tone === 'danger' ? 'assertive' : 'polite', 'aria-atomic': 'true' },
      el(
        'div',
        { class: 'toast-body' },
        el('i', { class: `bi ${TOAST_ICONS[tone]} toast-icon`, 'aria-hidden': 'true' }),
        el('div', { class: 'flex-grow-1', text: String(message) }),
        el('button', { type: 'button', class: 'btn-close btn-close-white ms-2', 'aria-label': 'Dismiss', 'data-bs-dismiss': 'toast' }),
      ),
    );
    host.appendChild(node);

    const delay = tone === 'danger' ? 9000 : tone === 'warning' ? 6000 : 3500;
    if (window.bootstrap && window.bootstrap.Toast) {
      node.addEventListener('hidden.bs.toast', () => node.remove());
      new window.bootstrap.Toast(node, { delay }).show();
    } else {
      node.classList.add('show');
      setTimeout(() => node.remove(), delay);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Confirm modal
  // ---------------------------------------------------------------------------------------------
  /** Resolves true when the user confirms. Title and message are rendered as text. */
  function confirmDialog({ title = 'Confirm', message = '', confirmText = 'Confirm', danger = false } = {}) {
    return new Promise((resolve) => {
      const modalEl = document.getElementById('confirm-modal');
      if (!modalEl || !window.bootstrap) {
        resolve(window.confirm(`${title}\n\n${message}`));
        return;
      }
      setText(document.getElementById('confirm-title'), title);
      setText(document.getElementById('confirm-message'), message);
      const ok = document.getElementById('confirm-ok');
      setText(ok, confirmText);
      ok.classList.toggle('btn-accent', !danger);
      ok.classList.toggle('btn-danger', danger);

      const modal = window.bootstrap.Modal.getOrCreateInstance(modalEl);
      let confirmed = false;
      const onOk = () => {
        confirmed = true;
        modal.hide();
      };
      const onHidden = () => {
        ok.removeEventListener('click', onOk);
        modalEl.removeEventListener('hidden.bs.modal', onHidden);
        resolve(confirmed);
      };
      ok.addEventListener('click', onOk);
      modalEl.addEventListener('hidden.bs.modal', onHidden);
      modal.show();
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Checklists (local, tester-only scratchpads; never reported as scenario results)
  // ---------------------------------------------------------------------------------------------
  function checklist(container, id, items) {
    if (!container) return;
    const key = `tp.checklist.${id}`;
    let saved = {};
    try {
      saved = JSON.parse(localStorage.getItem(key) || '{}') || {};
    } catch {
      saved = {};
    }
    const persist = () => {
      try {
        localStorage.setItem(key, JSON.stringify(saved));
      } catch {
        /* storage unavailable: the checklist still works for this session */
      }
    };

    const list = el('ul', { class: 'checklist' });
    items.forEach((text, index) => {
      const inputId = `chk-${id}-${index}`;
      const input = el('input', { class: 'form-check-input', type: 'checkbox', id: inputId });
      input.checked = Boolean(saved[index]);
      input.addEventListener('change', () => {
        saved[index] = input.checked;
        persist();
      });
      list.append(el('li', null, el('div', { class: 'form-check' }, input, el('label', { class: 'form-check-label', for: inputId, text }))));
    });

    const reset = el('button', { type: 'button', class: 'btn btn-xs btn-ghost', text: 'Uncheck all' });
    reset.addEventListener('click', () => {
      saved = {};
      persist();
      list.querySelectorAll('input').forEach((i) => (i.checked = false));
    });

    clear(container);
    container.append(
      el('div', { class: 'checklist-note' }, el('span', { text: 'Tester scratchpad only. Ticking items does not record a scenario verdict.' }), reset),
      list,
    );
  }

  TP.ui = { $, $$, el, clear, setText, setPill, pill, setRangeFill, latestWins, toast, confirm: confirmDialog, checklist };
})();
