/*
 * streaming.js — HTTP/HTTPS stream loading, developer override, status, checklist.
 *
 * The URL checks performed by the host are best-effort text checks, not SSRF protection. That limitation is shown
 * permanently at the top of the section.
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { el, clear, setText, setPill, toast } = TP.ui;
  const { run } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;

  const STATUS_REFRESH_MIN_MS = 500;
  const NETWORK_TONE = { idle: 'muted', connecting: 'info', buffering: 'info', ready: 'ok', reconnecting: 'warn', ended: 'muted', failed: 'bad' };

  const dom = {};
  let loading = false;
  let lastStatusAt = 0;
  let statusTimer = null;

  function resolveDom() {
    const ids = [
      'stream-limitation', 'stream-form', 'stream-url', 'stream-play', 'stream-allow-local', 'stream-local-warning',
      'btn-stream-load', 'btn-stream-cancel', 'stream-spinner', 'stream-load-icon', 'stream-load-label', 'stream-result',
      'btn-stream-refresh', 'btn-stream-to-scenarios', 'stream-checklist',
      'st-safety', 'st-override', 'st-ssrf', 'st-loading', 'st-active', 'st-network', 'st-bytes', 'st-error',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  // ---------------------------------------------------------------------------------------------
  // Status
  // ---------------------------------------------------------------------------------------------

  function renderStatus() {
    const s = state.streamStatus;
    const snap = state.snapshot;

    if (s) {
      setPill(dom['st-safety'], fmt.humanize(s.safetyMode), s.safetyMode === 'restricted' ? 'ok' : 'bad');
      setPill(dom['st-override'], s.allowLocalDeveloperMode ? 'ON (local hosts allowed)' : 'Off', s.allowLocalDeveloperMode ? 'bad' : 'muted');
      setPill(dom['st-ssrf'], s.ssrfProtectionComplete ? 'Yes' : 'No — best-effort checks only', s.ssrfProtectionComplete ? 'ok' : 'warn');
      setText(dom['st-loading'], s.loading ? 'Yes' : 'No');
      setText(dom['st-active'], s.activeUrl || '—');
      setText(dom['st-error'], s.lastError || '—');
      if (s.limitation) setText(dom['stream-limitation'], s.limitation);
    }

    // Network figures prefer the live snapshot (updated every ~200 ms) over the last status poll.
    const status = snap && snap.engineCreated ? snap.networkStatus : s ? s.networkStatus : null;
    const bytes = snap && snap.engineCreated ? snap.networkBytesBuffered : s ? s.networkBytesBuffered : null;
    if (status) setPill(dom['st-network'], fmt.humanize(status), NETWORK_TONE[status] || 'muted');
    setText(dom['st-bytes'], typeof bytes === 'number' ? fmt.bytes(bytes) : '—');
  }

  async function refreshStatus({ quiet = true } = {}) {
    lastStatusAt = performance.now();
    const result = await run('stream.status', [], { toast: !quiet, label: 'Stream status' });
    if (result.ok) {
      TP.core.setStreamStatus(result.value);
      reflectBusy(Boolean(result.value && result.value.loading) || loading);
    }
  }

  function scheduleStatusRefresh() {
    const wait = Math.max(0, STATUS_REFRESH_MIN_MS - (performance.now() - lastStatusAt));
    if (statusTimer) return;
    statusTimer = setTimeout(() => {
      statusTimer = null;
      refreshStatus();
    }, wait);
  }

  // ---------------------------------------------------------------------------------------------
  // Loading
  // ---------------------------------------------------------------------------------------------

  function reflectBusy(busy) {
    dom['btn-stream-load'].disabled = busy;
    dom['btn-stream-cancel'].disabled = !busy;
    dom['stream-spinner'].hidden = !busy;
    dom['stream-load-icon'].hidden = busy;
    setText(dom['stream-load-label'], busy ? 'Loading…' : dom['stream-allow-local'].checked ? 'Load stream (override ON)' : 'Load stream');
  }

  function showResult(kind, title, detail) {
    const box = clear(dom['stream-result']);
    box.append(
      el(
        'div',
        { class: `alert alert-${kind} py-2 small mb-0`, role: kind === 'danger' ? 'alert' : 'status' },
        el('strong', { text: title }),
        detail ? el('div', { class: 'mt-1 text-break', text: detail }) : null,
      ),
    );
  }

  async function loadStream(event) {
    event.preventDefault();
    if (loading) return;
    const url = dom['stream-url'].value.trim();
    if (!url) {
      showResult('warning', 'Enter a URL first.');
      dom['stream-url'].focus();
      return;
    }

    const allowLocal = dom['stream-allow-local'].checked;
    loading = true;
    reflectBusy(true);
    showResult('info', 'Loading…', 'Waiting for the transport to connect and the decoder to open the stream.');
    scheduleStatusRefresh();

    const result = await run('stream.load', [url, allowLocal, dom['stream-play'].checked], { toast: false });
    loading = false;

    if (!result.ok) {
      showResult('danger', 'Load failed', result.message);
      toast(`Stream load failed: ${result.message}`, 'danger');
    } else {
      const r = result.value || {};
      if (!r.accepted) showResult('warning', 'Blocked by pre-flight check', r.reason);
      else if (r.cancelled) showResult('info', 'Load cancelled', r.reason);
      else showResult('success', 'Stream loaded', r.reason);
      if (r.status) TP.core.setStreamStatus(r.status);
    }

    reflectBusy(false);
    await refreshStatus();
    await TP.core.refreshSnapshot();
  }

  async function cancelLoad() {
    const result = await run('stream.cancel', [], { label: 'Cancel' });
    if (result.ok) toast(result.value ? 'Cancelling the in-flight load…' : 'No load was in flight.', 'info');
    await refreshStatus();
  }

  async function onAllowLocalChanged() {
    const box = dom['stream-allow-local'];
    if (box.checked) {
      const ok = await TP.ui.confirm({
        title: 'Allow local / private hosts?',
        message:
          'This developer override lets the player request loopback, link-local and private-network addresses. ' +
          'It disables the only pre-flight host check and is not safe for untrusted URLs. Continue only if you are testing against a local server you control.',
        confirmText: 'Enable override',
        danger: true,
      });
      if (!ok) box.checked = false;
    }
    dom['stream-local-warning'].hidden = !box.checked;
    reflectBusy(loading);
  }

  // ---------------------------------------------------------------------------------------------
  // Checklist
  // ---------------------------------------------------------------------------------------------

  const CHECKLIST = [
    'Load a public HTTPS audio URL with the override off: network status moves Connecting → Buffering → Ready, audio plays, position advances.',
    'Try http://127.0.0.1/ and http://192.168.1.10/ with the override off: both are blocked with a reason.',
    'Repeat with the override on: the request is attempted (and fails or succeeds depending on your local server).',
    'Try a file:// URL: it is rejected.',
    'Start a load and press Cancel: the load stops, the player remains usable, status returns to idle.',
    'While streaming a long file, disconnect the network briefly: status shows Buffering / Reconnecting, then Ready, or Failed after retries. Nothing hangs.',
    'Pause and resume a stream, and seek if a duration is reported. Unknown duration shows —:—.',
    'Reminder: redirects and DNS names resolving to private addresses are not detected by these checks.',
  ];

  // ---------------------------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------------------------

  function init() {
    resolveDom();
    TP.ui.checklist(dom['stream-checklist'], 'streaming', CHECKLIST);

    const info = state.appInfo;
    if (info && info.urlSafetyLimitation) setText(dom['stream-limitation'], info.urlSafetyLimitation);

    dom['stream-form'].addEventListener('submit', loadStream);
    dom['btn-stream-cancel'].addEventListener('click', cancelLoad);
    dom['btn-stream-refresh'].addEventListener('click', () => refreshStatus({ quiet: false }));
    dom['stream-allow-local'].addEventListener('change', onAllowLocalChanged);
    dom['btn-stream-to-scenarios'].addEventListener('click', () => {
      if (TP.scenarios) TP.scenarios.showCategory('streaming');
      TP.core.go('scenarios');
    });
    document.querySelectorAll('[data-sample-url]').forEach((button) => {
      button.addEventListener('click', () => {
        dom['stream-url'].value = button.dataset.sampleUrl;
        dom['stream-url'].focus();
      });
    });

    TP.bus.on('streamStatus', renderStatus);
    TP.bus.on('snapshot', renderStatus);
    TP.bus.on('position', renderStatus);
    TP.bus.on('network', scheduleStatusRefresh);
    TP.bus.on('appInfo', () => {
      const i = state.appInfo;
      if (i && i.urlSafetyLimitation && !(state.streamStatus && state.streamStatus.limitation)) setText(dom['stream-limitation'], i.urlSafetyLimitation);
    });

    reflectBusy(false);
    renderStatus();
    return refreshStatus();
  }

  TP.streaming = { init, refresh: refreshStatus };
})();
