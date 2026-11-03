/*
 * diagnostics.js — live state table (Sonora vs derived vs host vs unavailable), process resources, event log, export.
 *
 * Source labels:
 *   sonora      value reported directly by the Sonora engine/player
 *   derived     computed by this test player (controller counters, or arithmetic on Sonora values)
 *   host        environment information from the host application
 *   unavailable not provided by the bridge, or unknown right now
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { el, clear, setText } = TP.ui;
  const { run } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;

  const LOG_MAX_RETAINED = 2000;
  const LOG_MAX_DOM_ROWS = 600;
  const SPARK_MAX_POINTS = 120;
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const LEVELS = { debug: 0, info: 1, warning: 2, error: 3 };

  const dom = {};

  // ---------------------------------------------------------------------------------------------
  // Live state table
  // ---------------------------------------------------------------------------------------------

  const UNAVAILABLE_NOTE = 'Not provided by this bridge';

  function ready(ctx) {
    return Boolean(ctx.snap) && ctx.snap.engineCreated && !ctx.snap.disposed;
  }

  const ROWS = [
    { group: 'Player (reported by Sonora)' },
    { label: 'Transport state', source: 'sonora', get: (c) => (ready(c) ? fmt.humanize(c.snap.state) : null) },
    { label: 'Source loaded', source: 'sonora', get: (c) => (ready(c) ? (c.snap.hasSource ? 'Yes' : 'No') : null) },
    { label: 'Source', source: 'sonora', get: (c) => (ready(c) ? (c.snap.sourcePath ? fmt.name(c.snap.sourcePath) : 'None') : null) },
    { label: 'Position (estimate, not sample-accurate)', source: 'sonora', get: (c) => (ready(c) ? `${fmt.time(c.snap.positionMs)}  (${Math.round(c.snap.positionMs)} ms)` : null) },
    { label: 'Decode frontier (buffered position)', source: 'sonora', get: (c) => (ready(c) ? `${fmt.time(c.snap.bufferedPositionMs)}  (${Math.round(c.snap.bufferedPositionMs)} ms)` : null) },
    { label: 'Duration', source: 'sonora', get: (c) => (ready(c) && c.snap.hasSource && c.snap.hasKnownDuration ? `${fmt.time(c.snap.durationMs)}  (${Math.round(c.snap.durationMs)} ms)` : null) },
    { label: 'Volume', source: 'sonora', get: (c) => (ready(c) ? `${Math.round(c.snap.volume * 100)}%` : null) },
    { label: 'Next-track slot', source: 'sonora', get: (c) => (ready(c) ? fmt.humanize(c.snap.nextTrackState) : null) },
    { label: 'Network transport', source: 'sonora', get: (c) => (ready(c) ? fmt.humanize(c.snap.networkStatus) : null) },
    { label: 'Network bytes buffered', source: 'sonora', get: (c) => (ready(c) ? fmt.bytes(c.snap.networkBytesBuffered) : null) },
    { label: 'Underruns (engine counter, since load)', source: 'sonora', get: (c) => (ready(c) ? String(c.snap.underrunCount) : null) },
    { label: 'Ring buffer (samples buffered / capacity)', source: 'sonora', get: (c) => (ready(c) ? `${c.snap.bufferedSamples} / ${c.snap.bufferCapacitySamples}` : null) },

    { group: 'Output (reported by Sonora)' },
    { label: 'Requested share mode', source: 'sonora', get: (c) => (c.out ? fmt.humanize(c.out.mode) : null) },
    { label: 'Effective share mode', source: 'sonora', get: (c) => (c.out && c.out.effectiveMode ? fmt.humanize(c.out.effectiveMode) : null) },
    { label: 'Fallback reason', source: 'sonora', get: (c) => (c.out ? fmt.humanize(c.out.fallbackReason || 'none') : null) },
    { label: 'Device status', source: 'sonora', get: (c) => (c.out ? fmt.humanize(c.out.deviceStatus) : null) },
    { label: 'Recovery intent', source: 'sonora', get: (c) => (c.out ? fmt.humanize(c.out.recoveryIntent) : null) },
    { label: 'Active device', source: 'sonora', get: (c) => (c.out && c.out.activeDeviceId ? (TP.output && TP.output.deviceName(c.out.activeDeviceId)) || c.out.activeDeviceId : null) },
    { label: 'Effective latency (last config event)', source: 'sonora', get: (c) => (c.out && typeof c.out.effectiveLatencyMs === 'number' ? `${c.out.effectiveLatencyMs.toFixed(1)} ms` : null) },

    { group: 'Derived by this app' },
    { label: 'Current item id', source: 'derived', get: (c) => (ready(c) ? c.snap.currentItemId || 'None' : null) },
    { label: 'Queue position', source: 'derived', get: (c) => (ready(c) ? (c.snap.queueIndex >= 0 ? `${c.snap.queueIndex + 1} of ${c.snap.queueLength}` : `none of ${c.snap.queueLength}`) : null) },
    { label: 'Session generation', source: 'derived', get: (c) => (ready(c) ? String(c.snap.generation) : null) },
    { label: 'Gapless preload setting', source: 'derived', get: (c) => (ready(c) ? (c.snap.gaplessEnabled ? 'Enabled' : 'Disabled') : null) },
    { label: 'Track transitions accepted', source: 'derived', get: (c) => (ready(c) ? String(c.snap.transitionsObserved) : null) },
    { label: 'Completion events observed', source: 'derived', get: (c) => (ready(c) ? String(c.snap.completionsObserved) : null) },
    { label: 'Queue advances via completion', source: 'derived', get: (c) => (ready(c) ? String(c.snap.completionAdvances) : null) },
    { label: 'Error events observed', source: 'derived', get: (c) => (ready(c) ? String(c.snap.errorsObserved) : null) },
    { label: 'Underrun events observed', source: 'derived', get: (c) => (ready(c) ? String(c.snap.underrunEventsObserved) : null) },
    {
      label: 'Ring buffer fill',
      source: 'derived',
      get: (c) => (ready(c) && c.snap.bufferCapacitySamples > 0 ? `${((c.snap.bufferedSamples / c.snap.bufferCapacitySamples) * 100).toFixed(1)}%` : null),
    },
    {
      label: 'Decode lead (frontier − position)',
      source: 'derived',
      get: (c) => (ready(c) && c.snap.hasSource ? `${((c.snap.bufferedPositionMs - c.snap.positionMs) / 1000).toFixed(2)} s` : null),
    },

    { group: 'Host' },
    { label: 'Test Player version', source: 'host', get: (c) => (c.env ? c.env.testPlayerVersion || null : null) },
    { label: 'Sonora version', source: 'host', get: (c) => (c.env ? c.env.sonoraVersion || null : null) },
    { label: 'Operating system', source: 'host', get: (c) => (c.env ? c.env.os || null : null) },
    { label: '.NET runtime', source: 'host', get: (c) => (c.env ? c.env.runtime || null : null) },
    { label: 'Process architecture', source: 'host', get: (c) => (c.env ? c.env.processArchitecture || null : null) },

    { group: 'Not exposed by this bridge' },
    { label: 'Audio callback CPU load', source: 'unavailable', get: () => null, note: UNAVAILABLE_NOTE },
    { label: 'Output peak / level meter', source: 'unavailable', get: () => null, note: UNAVAILABLE_NOTE },
    { label: 'Limiter gain reduction', source: 'unavailable', get: () => null, note: UNAVAILABLE_NOTE },
  ];

  const rowRefs = []; // { def, tr, valueCell, badge } or { def, tr } for group rows

  function buildStateTable() {
    const body = clear(dom['diag-state-body']);
    rowRefs.length = 0;
    for (const def of ROWS) {
      if (def.group) {
        const tr = el('tr', { class: 'group-row' }, el('td', { colspan: '3', text: def.group }));
        body.append(tr);
        rowRefs.push({ def, tr });
        continue;
      }
      const valueCell = el('td', { class: 'value' });
      const badge = el('span', { class: 'src-badge' });
      const tr = el('tr', null, el('td', { class: 'metric', text: def.label }), valueCell, el('td', { class: 'text-end' }, badge));
      body.append(tr);
      rowRefs.push({ def, tr, valueCell, badge });
    }
  }

  function renderStateTable() {
    const ctx = {
      snap: state.snapshot,
      out: state.output,
      env: state.appInfo ? state.appInfo.environment : null,
    };
    const filter = dom['diag-filter-source'].value;
    let groupRef = null;
    let groupVisible = false;
    const closeGroup = () => {
      if (groupRef) groupRef.tr.hidden = !groupVisible;
    };

    for (const ref of rowRefs) {
      if (ref.def.group) {
        closeGroup();
        groupRef = ref;
        groupVisible = false;
        continue;
      }
      const text = ref.def.get(ctx);
      const available = text != null;
      const source = available ? ref.def.source : 'unavailable';
      setText(ref.valueCell, available ? text : ref.def.note || 'Unavailable');
      ref.valueCell.classList.toggle('is-unavailable', !available);
      if (ref.badge.dataset.source !== source) {
        ref.badge.dataset.source = source;
        setText(ref.badge, source === 'sonora' ? 'Sonora' : source === 'derived' ? 'Derived' : source === 'host' ? 'Host' : 'Unavailable');
      }
      const visible = filter === 'all' || filter === source;
      ref.tr.hidden = !visible;
      if (visible) groupVisible = true;
    }
    closeGroup();
  }

  // ---------------------------------------------------------------------------------------------
  // Process resources
  // ---------------------------------------------------------------------------------------------

  const wsHistory = [];

  function renderResources() {
    const r = state.resources;
    if (!r) return;
    const set = (key, text) => {
      const cell = dom['res-body'].querySelector(`[data-res="${key}"]`);
      if (cell) setText(cell, text);
    };
    set('workingSetBytes', fmt.bytes(r.workingSetBytes));
    set('privateBytes', fmt.bytes(r.privateBytes));
    set('gcTotalMemoryBytes', fmt.bytes(r.gcTotalMemoryBytes));
    set('gc', `${r.gen0Collections} / ${r.gen1Collections} / ${r.gen2Collections}`);
    set('handleCount', String(r.handleCount));
    set('threadCount', String(r.threadCount));
    set('uptimeSeconds', fmt.elapsedSeconds(r.uptimeSeconds));
    renderSparkline();
  }

  function renderSparkline() {
    const svg = dom['res-spark'];
    clear(svg);
    if (wsHistory.length < 2) {
      setText(dom['res-range'], 'collecting…');
      return;
    }
    const min = Math.min(...wsHistory);
    const max = Math.max(...wsHistory);
    const span = max - min;
    const points = wsHistory
      .map((v, i) => {
        const x = (i / (wsHistory.length - 1)) * 200;
        const y = span === 0 ? 20 : 37 - ((v - min) / span) * 34;
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(' ');
    const line = document.createElementNS(SVG_NS, 'polyline');
    line.setAttribute('points', points);
    svg.append(line);
    setText(dom['res-range'], `${fmt.bytes(min)} – ${fmt.bytes(max)} (last ${wsHistory.length})`);
  }

  function onResources() {
    const r = state.resources;
    if (r && typeof r.workingSetBytes === 'number') {
      wsHistory.push(r.workingSetBytes);
      if (wsHistory.length > SPARK_MAX_POINTS) wsHistory.shift();
    }
    if (state.section === 'diagnostics') renderResources();
  }

  // ---------------------------------------------------------------------------------------------
  // Event log
  // ---------------------------------------------------------------------------------------------

  const log = {
    events: [],
    lastSeq: 0,
    paused: false,
    pendingWhilePaused: 0,
    visible: 0,
    categories: new Set(),
    scrollQueued: false,
  };

  function minLevel() {
    return Number(dom['log-level'].value);
  }

  function matches(e) {
    if ((LEVELS[e.level] ?? 0) < minLevel()) return false;
    const category = dom['log-category'].value;
    if (category && e.category !== category) return false;
    const query = dom['log-search'].value.trim().toLowerCase();
    if (query && !`${e.name} ${e.message}`.toLowerCase().includes(query)) return false;
    return true;
  }

  function dataText(e) {
    return e.data ? Object.entries(e.data).map(([k, v]) => `${k}=${v}`).join('  ') : '';
  }

  function makeLogRow(e) {
    const data = dataText(e);
    const row = el(
      'div',
      { class: `log-row level-${LEVELS[e.level] === undefined ? 'info' : e.level}`, title: data ? `${e.message}\n${data}` : e.message },
      el('span', { class: 't', text: fmt.localTime(e.utcTime, true) }),
      el('span', { class: 'lvl', text: e.level }),
      el('span', { class: 'cat', text: e.category, title: e.category }),
      el('span', { class: 'nm', text: e.name, title: e.name }),
      el('span', { class: 'msg' }, e.message, data ? el('span', { class: 'data', text: `   ${data}` }) : null),
    );
    row.addEventListener('click', () => row.classList.toggle('expanded'));
    return row;
  }

  function updateLogCount() {
    const badge = dom['log-count'];
    setText(badge, `${log.visible}/${log.events.length}`);
    badge.title = `${log.visible} shown of ${log.events.length} retained events`;
    const pause = dom['btn-log-pause'];
    const label = pause.querySelector('span');
    if (log.paused) setText(label, log.pendingWhilePaused > 0 ? `Resume (${log.pendingWhilePaused} new)` : 'Resume');
    else setText(label, 'Pause');
    pause.querySelector('i').className = `bi ${log.paused ? 'bi-play-fill' : 'bi-pause-fill'}`;
    pause.setAttribute('aria-pressed', String(log.paused));
  }

  function showEmptyIfNeeded() {
    const list = dom['log-list'];
    if (list.querySelector('.log-row')) return;
    if (!list.querySelector('.log-empty')) list.append(el('div', { class: 'log-empty', text: log.events.length === 0 ? 'No events yet.' : 'No events match the current filters.' }));
  }

  function renderLogAll() {
    const list = clear(dom['log-list']);
    const shown = log.events.filter(matches);
    log.visible = shown.length;
    const slice = shown.length > LOG_MAX_DOM_ROWS ? shown.slice(shown.length - LOG_MAX_DOM_ROWS) : shown;
    const frag = document.createDocumentFragment();
    for (const e of slice) frag.append(makeLogRow(e));
    list.append(frag);
    showEmptyIfNeeded();
    queueScroll();
    updateLogCount();
  }

  function queueScroll() {
    if (!dom['log-autoscroll'].checked || log.scrollQueued) return;
    log.scrollQueued = true;
    requestAnimationFrame(() => {
      log.scrollQueued = false;
      dom['log-list'].scrollTop = dom['log-list'].scrollHeight;
    });
  }

  function syncCategories(category) {
    if (log.categories.has(category)) return;
    log.categories.add(category);
    const select = dom['log-category'];
    const current = select.value;
    clear(select).append(el('option', { value: '', text: 'All categories' }));
    for (const name of Array.from(log.categories).sort()) select.append(el('option', { value: name, text: name }));
    select.value = current;
  }

  function addLogEvent(e) {
    if (!e || typeof e.seq !== 'number' || e.seq <= log.lastSeq) return; // duplicate (backfill vs push) or malformed
    log.lastSeq = e.seq;
    log.events.push(e);
    if (log.events.length > LOG_MAX_RETAINED) log.events.splice(0, log.events.length - LOG_MAX_RETAINED);
    syncCategories(e.category);

    if (log.paused) {
      log.pendingWhilePaused += 1;
      updateLogCount();
      return;
    }
    if (!matches(e)) {
      updateLogCount();
      return;
    }
    const list = dom['log-list'];
    const empty = list.querySelector('.log-empty');
    if (empty) empty.remove();
    list.append(makeLogRow(e));
    log.visible += 1;
    while (list.childElementCount > LOG_MAX_DOM_ROWS) list.firstElementChild.remove();
    queueScroll();
    updateLogCount();
  }

  async function backfillLog() {
    const result = await run('diagnostics.getEvents', [0], { toast: false });
    if (!result.ok || !Array.isArray(result.value)) return;
    for (const e of result.value) addLogEvent(e);
    if (!log.paused) renderLogAll();
  }

  function wireLog() {
    const rerender = () => {
      if (!log.paused) renderLogAll();
    };
    dom['log-level'].addEventListener('change', rerender);
    dom['log-category'].addEventListener('change', rerender);
    dom['log-search'].addEventListener('input', rerender);
    dom['log-autoscroll'].addEventListener('change', queueScroll);
    dom['btn-log-pause'].addEventListener('click', () => {
      log.paused = !log.paused;
      if (!log.paused) {
        log.pendingWhilePaused = 0;
        renderLogAll();
      }
      updateLogCount();
    });
    dom['btn-log-clear'].addEventListener('click', async () => {
      const result = await run('diagnostics.clear', [], { label: 'Clear log' });
      if (!result.ok) return;
      log.events = [];
      log.pendingWhilePaused = 0;
      renderLogAll();
    });
    TP.bus.on('diagEvent', addLogEvent);
  }

  // ---------------------------------------------------------------------------------------------
  // Export
  // ---------------------------------------------------------------------------------------------

  function exportFormat() {
    return document.querySelector('input[name="export-format"]:checked').value;
  }

  function setExportStatus(text, tone) {
    const node = dom['export-status'];
    setText(node, text);
    node.classList.toggle('text-danger', tone === 'bad');
    node.classList.toggle('text-success', tone === 'ok');
  }

  async function requestExport() {
    const result = await run('diagnostics.export', [dom['export-sensitive'].checked, exportFormat()], { label: 'Export' });
    return result.ok && typeof result.value === 'string' ? result.value : null;
  }

  async function previewExport() {
    const content = await requestExport();
    if (content == null) return null;
    dom['export-preview'].value = content;
    setExportStatus(`Preview generated (${content.length.toLocaleString()} characters, ${dom['export-sensitive'].checked ? 'unredacted' : 'redacted'}).`, 'ok');
    return content;
  }

  async function copyText(text) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      const ta = el('textarea', { 'aria-hidden': 'true', style: { position: 'fixed', opacity: '0', left: '-9999px' } });
      ta.value = text;
      document.body.append(ta);
      ta.select();
      let ok = false;
      try {
        ok = document.execCommand('copy');
      } catch {
        ok = false;
      }
      ta.remove();
      return ok;
    }
  }

  async function copyExport() {
    const content = await requestExport();
    if (content == null) return;
    dom['export-preview'].value = content;
    const ok = await copyText(content);
    setExportStatus(ok ? 'Copied to clipboard.' : 'Could not access the clipboard. Select the preview text and copy it manually.', ok ? 'ok' : 'bad');
    if (!ok) TP.ui.toast('Clipboard unavailable. Copy from the preview box instead.', 'warning');
  }

  function defaultFileName(format) {
    const d = new Date();
    const p = (n) => String(n).padStart(2, '0');
    return `sonora-diagnostics-${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}.${format === 'text' ? 'txt' : 'json'}`;
  }

  async function saveExport() {
    const format = exportFormat();
    let path = null;
    if (state.dialogsEnabled) {
      const picked = await TP.bridge.dialogs.saveFile({
        title: 'Save diagnostics export',
        defaultFileName: defaultFileName(format),
        filters: [format === 'text' ? { name: 'Text', extensions: ['txt'] } : { name: 'JSON', extensions: ['json'] }, { name: 'All files', extensions: ['*'] }],
      });
      if (!picked.ok || !picked.value) return;
      path = picked.value;
    } else {
      dom['export-path-wrap'].hidden = false;
      path = dom['export-path'].value.trim();
      if (!path) {
        setExportStatus('File dialogs are unavailable. Enter a full output path and press Save again.', 'bad');
        dom['export-path'].focus();
        return;
      }
    }
    const result = await run('diagnostics.exportToFile', [path, dom['export-sensitive'].checked, format], { label: 'Save export' });
    if (result.ok) {
      setExportStatus(`Saved ${result.value || fmt.name(path)} (${dom['export-sensitive'].checked ? 'unredacted' : 'redacted'}).`, 'ok');
      TP.ui.toast(`Export saved: ${result.value || fmt.name(path)}`, 'success');
    } else {
      setExportStatus(`Save failed: ${result.message}`, 'bad');
    }
  }

  async function onSensitiveChanged() {
    const box = dom['export-sensitive'];
    if (!box.checked) return;
    const ok = await TP.ui.confirm({
      title: 'Include sensitive data?',
      message: 'The export will contain full file paths, complete URLs (including query strings and credentials) and your user profile folder. Review it before sharing.',
      confirmText: 'Include sensitive data',
      danger: true,
    });
    if (!ok) box.checked = false;
  }

  // ---------------------------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------------------------

  function renderIfVisible() {
    if (state.section !== 'diagnostics') return;
    renderStateTable();
  }

  function gaplessPcmParams() {
    const dir = (dom['gapless-pcm-dir'].value || '').trim();
    const start = Number(dom['gapless-pcm-start-ms'].value);
    const windowMs = Number(dom['gapless-pcm-window-ms'].value);
    return {
      dir,
      start: Number.isFinite(start) && start >= 0 ? start : 3000,
      windowMs: Number.isFinite(windowMs) && windowMs > 0 ? windowMs : 500,
    };
  }

  function setGaplessPcmStatus(text, tone) {
    const el = dom['gapless-pcm-status'];
    el.textContent = text || '';
    el.classList.toggle('text-danger', tone === 'bad');
    el.classList.toggle('text-success', tone === 'ok');
  }

  async function captureGaplessPromote() {
    const p = gaplessPcmParams();
    if (!p.dir) {
      setGaplessPcmStatus('Set an output folder first.', 'bad');
      return;
    }
    setGaplessPcmStatus('checkingPrerequisites…', null);
    const result = await run(
      'diagnostics.gaplessPcm.captureAfterPromote',
      [p.dir, p.start, p.windowMs],
      { label: 'Gapless PCM promote capture' });
    if (result.ok) {
      dom['gapless-pcm-result'].value = JSON.stringify(result.value, null, 2);
      setGaplessPcmStatus('captureCompleted — listen and note whether B was distorted.', 'ok');
      TP.ui.toast('Promote mid-B PCM capture saved.', 'success');
    } else {
      setGaplessPcmStatus(result.message || 'Promote capture failed.', 'bad');
      dom['gapless-pcm-result'].value = result.message || '';
    }
  }

  async function captureGaplessManual() {
    const p = gaplessPcmParams();
    if (!p.dir) {
      setGaplessPcmStatus('Set an output folder first.', 'bad');
      return;
    }
    setGaplessPcmStatus('Waiting for Position on current track…', null);
    const result = await run(
      'diagnostics.gaplessPcm.captureManual',
      [p.dir, p.start, p.windowMs],
      { label: 'Gapless PCM manual capture' });
    if (result.ok) {
      dom['gapless-pcm-result'].value = JSON.stringify(result.value, null, 2);
      setGaplessPcmStatus('Manual capture saved.', 'ok');
      TP.ui.toast('Manual mid-B PCM capture saved.', 'success');
    } else {
      setGaplessPcmStatus(result.message || 'Manual capture failed.', 'bad');
    }
  }

  async function compareGaplessPcm() {
    setGaplessPcmStatus('Comparing last promote vs manual captures…', null);
    const result = await run('diagnostics.gaplessPcm.compareLast', [100], { label: 'Gapless PCM compare' });
    if (result.ok) {
      dom['gapless-pcm-result'].value = JSON.stringify(result.value, null, 2);
      const v = result.value || {};
      if (v.suitable === false) {
        setGaplessPcmStatus(v.reason || 'Captures unsuitable for comparison.', 'bad');
      } else if (v.alignmentInconclusive) {
        setGaplessPcmStatus('Alignment inconclusive (weak NCC). Do not treat as a PCM match.', 'bad');
      } else if (v.alignedMatch) {
        setGaplessPcmStatus('Aligned managed-boundary PCM matches. Pair with your listen notes — match alone does not prove post-boundary cause.', 'ok');
      } else {
        setGaplessPcmStatus('Aligned PCM still diverges after best lag.', 'bad');
      }
    } else {
      setGaplessPcmStatus(result.message || 'Compare failed.', 'bad');
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Listening A/B (interactive observation)
  // ---------------------------------------------------------------------------------------------

  let listeningAbAttemptId = null;
  let listeningAbSelectedClass = null;

  function setListeningAbStatus(text, tone) {
    const el = dom['listening-ab-status'];
    if (!el) return;
    el.textContent = text || '';
    el.classList.toggle('text-danger', tone === 'bad');
    el.classList.toggle('text-success', tone === 'ok');
  }

  function showListeningAbObserve(prompt, attemptId) {
    listeningAbAttemptId = attemptId || null;
    listeningAbSelectedClass = null;
    const box = dom['listening-ab-observe'];
    const promptEl = dom['listening-ab-prompt'];
    const cont = dom['btn-listening-ab-continue'];
    if (promptEl) promptEl.textContent = prompt || 'How did that playback sound?';
    if (box) box.hidden = false;
    if (cont) cont.disabled = true;
    document.querySelectorAll('.listening-ab-class').forEach((btn) => {
      btn.classList.remove('active');
    });
  }

  function hideListeningAbObserve() {
    listeningAbAttemptId = null;
    listeningAbSelectedClass = null;
    const box = dom['listening-ab-observe'];
    if (box) box.hidden = true;
  }

  function resolveListeningAbPaths() {
    let a = (dom['listening-ab-a'] && dom['listening-ab-a'].value || '').trim();
    let b = (dom['listening-ab-b'] && dom['listening-ab-b'].value || '').trim();
    const queue = (TP.state && TP.state.queue) || [];
    if (!a && queue[0] && queue[0].path) a = queue[0].path;
    if (!b && queue[1] && queue[1].path) b = queue[1].path;
    if (!b && queue[0] && queue[0].path && a && a !== queue[0].path) b = queue[0].path;
    return { a, b };
  }

  async function runListeningAbTest() {
    const paths = resolveListeningAbPaths();
    const dir = (dom['gapless-pcm-dir'].value || '').trim()
      || (window.TP && TP.state && TP.state.defaultCaptureDir)
      || '';
    let outDir = dir;
    if (!outDir) {
      // Sensible default under the user profile — host expands when writing.
      outDir = '%USERPROFILE%\\sonora-listening-ab';
    }
    if (!paths.a || !paths.b) {
      setListeningAbStatus('Set Track A and Track B paths (or queue two files).', 'bad');
      return;
    }
    setListeningAbStatus('Running listening A/B… follow on-screen prompts after each scenario.', null);
    hideListeningAbObserve();
    if (dom['listening-ab-result']) dom['listening-ab-result'].value = '';
    const result = await run(
      'diagnostics.listeningAb.run',
      [paths.a, paths.b, outDir],
      { label: 'Listening A/B test', timeoutMs: 45 * 60 * 1000 });
    hideListeningAbObserve();
    if (result.ok) {
      const v = result.value || {};
      const summary = v.summaryLine || JSON.stringify(v, null, 2);
      if (dom['listening-ab-result']) {
        dom['listening-ab-result'].value = typeof summary === 'string'
          ? summary + (v.caution ? '\n\n' + v.caution : '') + '\n\n' + JSON.stringify(v, null, 2)
          : JSON.stringify(v, null, 2);
      }
      setListeningAbStatus(v.summaryLine || 'Listening A/B completed.', 'ok');
      TP.ui.toast('Listening A/B report saved.', 'success');
    } else {
      setListeningAbStatus(result.message || 'Listening A/B failed.', 'bad');
      if (dom['listening-ab-result']) dom['listening-ab-result'].value = result.message || '';
    }
  }

  async function submitListeningAbObservation() {
    if (!listeningAbAttemptId || !listeningAbSelectedClass) {
      setListeningAbStatus('Select Clean, Static, or Unsure before Continue.', 'bad');
      return;
    }
    const result = await run(
      'diagnostics.listeningAb.submitObservation',
      [listeningAbAttemptId, listeningAbSelectedClass, null],
      { label: 'Submit listening observation' });
    if (result.ok && result.value && result.value.accepted) {
      setListeningAbStatus(`Recorded ${listeningAbSelectedClass} for ${listeningAbAttemptId}.`, 'ok');
      hideListeningAbObserve();
    } else {
      setListeningAbStatus(
        (result.value && result.value.accepted === false)
          ? 'Observation rejected (attempt id mismatch or no pending prompt).'
          : (result.message || 'Could not submit observation.'),
        'bad');
    }
  }

  function resolveDom() {
    const ids = [
      'diag-filter-source', 'diag-state-body', 'btn-diag-refresh', 'res-body', 'res-spark', 'res-range', 'log-level',
      'log-category', 'log-search', 'btn-log-pause', 'log-autoscroll', 'btn-log-clear', 'log-count', 'log-list',
      'export-sensitive', 'btn-export-preview', 'btn-export-copy', 'btn-export-save', 'export-path-wrap', 'export-path',
      'export-preview', 'export-status',
      'gapless-pcm-dir', 'gapless-pcm-start-ms', 'gapless-pcm-window-ms',
      'btn-gapless-pcm-promote', 'btn-gapless-pcm-manual', 'btn-gapless-pcm-compare',
      'gapless-pcm-status', 'gapless-pcm-result',
      'listening-ab-a', 'listening-ab-b', 'btn-listening-ab-run', 'listening-ab-status',
      'listening-ab-observe', 'listening-ab-prompt', 'btn-listening-ab-continue', 'listening-ab-result',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  function init() {
    resolveDom();
    buildStateTable();
    wireLog();

    dom['diag-filter-source'].addEventListener('change', renderStateTable);
    dom['btn-diag-refresh'].addEventListener('click', async () => {
      await TP.core.refreshSnapshot();
      await TP.output.refresh({ quiet: true });
      renderStateTable();
    });
    dom['btn-export-preview'].addEventListener('click', previewExport);
    dom['btn-export-copy'].addEventListener('click', copyExport);
    dom['btn-export-save'].addEventListener('click', saveExport);
    dom['export-sensitive'].addEventListener('change', onSensitiveChanged);
    dom['btn-gapless-pcm-promote'].addEventListener('click', captureGaplessPromote);
    dom['btn-gapless-pcm-manual'].addEventListener('click', captureGaplessManual);
    dom['btn-gapless-pcm-compare'].addEventListener('click', compareGaplessPcm);
    if (dom['btn-listening-ab-run']) {
      dom['btn-listening-ab-run'].addEventListener('click', runListeningAbTest);
    }
    document.querySelectorAll('.listening-ab-class').forEach((btn) => {
      btn.addEventListener('click', () => {
        listeningAbSelectedClass = btn.getAttribute('data-class');
        document.querySelectorAll('.listening-ab-class').forEach((b) => b.classList.remove('active'));
        btn.classList.add('active');
        if (dom['btn-listening-ab-continue']) dom['btn-listening-ab-continue'].disabled = false;
      });
    });
    if (dom['btn-listening-ab-continue']) {
      dom['btn-listening-ab-continue'].addEventListener('click', submitListeningAbObservation);
    }

    TP.bus.on('gaplessPcmPhase', (payload) => {
      if (!payload) return;
      const phase = payload.phase || '';
      const message = payload.message || phase;
      setGaplessPcmStatus(`${phase}: ${message}`, phase === 'captureFailed' ? 'bad' : null);
    });

    TP.bus.on('listeningAbPhase', (payload) => {
      if (!payload) return;
      setListeningAbStatus(`${payload.phase || ''}: ${payload.message || ''}`, payload.phase === 'failed' ? 'bad' : null);
    });

    TP.bus.on('listeningAbAwaitObservation', (payload) => {
      if (!payload) return;
      showListeningAbObserve(payload.prompt, payload.attemptId);
      setListeningAbStatus('Waiting for your listening classification…', null);
    });

    TP.bus.on('listeningAbObservationReceived', () => {
      hideListeningAbObserve();
    });

    TP.bus.on('snapshot', renderIfVisible);
    TP.bus.on('position', renderIfVisible);
    TP.bus.on('output', renderIfVisible);
    TP.bus.on('appInfo', renderIfVisible);
    TP.bus.on('resources', onResources);
    TP.bus.on('section', (name) => {
      if (name === 'diagnostics') {
        renderStateTable();
        renderResources();
        queueScroll();
      }
    });

    renderStateTable();
    renderLogAll();
    return Promise.all([
      backfillLog(),
      run('diagnostics.resources', [], { toast: false }).then((r) => {
        if (r.ok) {
          TP.core.setResources(r.value, null);
          renderResources();
        }
      }),
    ]);
  }

  TP.diagnostics = { init, renderStateTable };
})();
