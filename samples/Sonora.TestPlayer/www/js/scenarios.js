/*
 * scenarios.js — manual scenario catalog, tester-recorded verdicts, soak session controls.
 *
 * Verdicts are recorded ONLY when the tester presses a status button. Nothing in this file (or elsewhere in the UI)
 * infers, suggests or sets "pass" from counters, soak numbers or diagnostics events.
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { el, clear, setText, toast } = TP.ui;
  const { run } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;

  const STATUSES = [
    { value: 'pass', label: 'Pass', tone: 'ok' },
    { value: 'fail', label: 'Fail', tone: 'bad' },
    { value: 'inconclusive', label: 'Inconclusive', tone: 'warn' },
    { value: 'notRun', label: 'Not run', tone: 'muted' },
  ];
  const STATUS_LABEL = Object.fromEntries(STATUSES.map((s) => [s.value, s.label]));
  const STATUS_TONE = Object.fromEntries(STATUSES.map((s) => [s.value, s.tone]));
  const CATEGORY_LABEL = { dsp: 'DSP' };
  const SOAK_TICK_MS = 1000;

  const dom = {};
  let catalog = [];
  const results = new Map(); // scenarioId -> ScenarioResult
  const cards = new Map(); // scenarioId -> refs
  const headings = new Map(); // category -> element
  let categoryFilter = '';
  let soakReceivedAt = 0;
  let soakTimer = null;

  const categoryLabel = (c) => CATEGORY_LABEL[c] || fmt.humanize(c);
  const statusOf = (id) => (results.get(id) ? results.get(id).status : 'notRun');

  function resolveDom() {
    const ids = [
      'scn-summary', 'scn-bar-pass', 'scn-bar-fail', 'scn-bar-inconclusive', 'scn-category-pills', 'scn-status-filter',
      'btn-scn-reset', 'scn-list', 'soak-state', 'btn-soak-start', 'btn-soak-stop', 'soak-elapsed', 'soak-started',
      'soak-errors', 'soak-underruns', 'soak-ws-start', 'soak-ws-latest', 'soak-ws-peak', 'soak-ws-delta',
      'soak-note-input', 'btn-soak-note', 'soak-notes',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  // ---------------------------------------------------------------------------------------------
  // Catalog / cards
  // ---------------------------------------------------------------------------------------------

  function buildCards() {
    const list = clear(dom['scn-list']);
    cards.clear();
    headings.clear();

    const order = [];
    for (const def of catalog) if (!order.includes(def.category)) order.push(def.category);

    for (const category of order) {
      const heading = el('div', { class: 'scn-category' }, el('span', { text: categoryLabel(category) }), el('span', { 'data-count': '' }));
      headings.set(category, heading);
      list.append(heading);
      for (const def of catalog.filter((d) => d.category === category)) list.append(buildCard(def));
    }
    buildCategoryPills(order);
  }

  function buildCard(def) {
    const pill = el('span', { class: 'tp-pill' });
    const recorded = el('span', { class: 'small text-body-secondary' });
    const notes = el('textarea', { class: 'form-control form-control-sm', rows: '2', placeholder: 'Notes (what you observed, device, file used…)', 'aria-label': `Notes for ${def.title}` });
    const saveNotes = el('button', { type: 'button', class: 'btn btn-sm btn-outline-secondary', disabled: true, text: 'Save notes' });

    const buttons = {};
    const group = el('div', { class: 'btn-group status-group', role: 'group', 'aria-label': `Verdict for ${def.title}` });
    for (const status of STATUSES) {
      const button = el('button', { type: 'button', class: 'btn btn-outline-secondary', 'aria-pressed': 'false', text: status.label });
      button.addEventListener('click', () => record(def.id, status.value));
      buttons[status.value] = button;
      group.append(button);
    }

    const card = el(
      'details',
      { class: 'scn-card', dataset: { id: def.id, status: 'notRun' } },
      el(
        'summary',
        null,
        el('i', { class: 'bi bi-chevron-right chev', 'aria-hidden': 'true' }),
        el('span', { class: 'scn-title', text: def.title }),
        def.requiresHardware ? el('span', { class: 'tp-pill', 'data-tone': 'info', text: 'Hardware', title: 'Needs a real audio device or specific hardware' }) : null,
        el('span', { class: 'scn-id d-none d-md-inline', text: def.id }),
        pill,
      ),
      el(
        'div',
        { class: 'scn-body' },
        el('ol', { class: 'scn-steps' }, (def.steps || []).map((step) => el('li', { text: step }))),
        el('div', { class: 'scn-expected' }, el('strong', { text: 'Expected: ' }), def.expected),
        el('div', { class: 'd-flex flex-wrap align-items-center gap-3 mb-2' }, group, recorded),
        notes,
        el('div', { class: 'mt-2' }, saveNotes),
      ),
    );

    const ref = { def, card, pill, recorded, notes, saveNotes, buttons, savedNotes: '' };
    notes.addEventListener('input', () => {
      saveNotes.disabled = notes.value.trim() === ref.savedNotes.trim();
    });
    saveNotes.addEventListener('click', () => record(def.id, statusOf(def.id)));
    cards.set(def.id, ref);
    return card;
  }

  function updateCard(id) {
    const ref = cards.get(id);
    if (!ref) return;
    const result = results.get(id);
    const status = result ? result.status : 'notRun';
    ref.card.dataset.status = status;
    TP.ui.setPill(ref.pill, STATUS_LABEL[status] || fmt.humanize(status), STATUS_TONE[status] || 'muted');
    for (const [value, button] of Object.entries(ref.buttons)) {
      const selected = value === status;
      button.setAttribute('aria-pressed', String(selected));
      button.className = `btn btn-outline-secondary${selected ? ` sel-${value}` : ''}`;
    }
    ref.savedNotes = (result && result.notes) || '';
    if (document.activeElement !== ref.notes) ref.notes.value = ref.savedNotes;
    ref.saveNotes.disabled = ref.notes.value.trim() === ref.savedNotes.trim();
    setText(ref.recorded, result && result.recordedAtUtc ? `Recorded ${new Date(result.recordedAtUtc).toLocaleString()}` : status === 'notRun' ? 'Not recorded' : '');
  }

  async function record(id, status) {
    const ref = cards.get(id);
    const notes = ref ? ref.notes.value.trim() : '';
    const result = await run('scenarios.record', [id, status, notes || null], { label: 'Record result' });
    if (!result.ok) return;
    results.set(id, result.value);
    updateCard(id);
    renderSummary();
    applyFilters();
  }

  // ---------------------------------------------------------------------------------------------
  // Filters / summary
  // ---------------------------------------------------------------------------------------------

  function buildCategoryPills(order) {
    const host = clear(dom['scn-category-pills']);
    const make = (value, label, count) => {
      const button = el('button', { type: 'button', class: 'btn btn-xs btn-chip', dataset: { category: value }, 'aria-pressed': 'false' }, label, el('span', { class: 'ms-1 text-body-secondary', text: String(count) }));
      button.addEventListener('click', () => {
        categoryFilter = value;
        applyFilters();
      });
      return button;
    };
    host.append(make('', 'All', catalog.length));
    for (const category of order) host.append(make(category, categoryLabel(category), catalog.filter((d) => d.category === category).length));
  }

  function applyFilters() {
    const statusFilter = dom['scn-status-filter'].value;
    const visibleByCategory = new Map();
    for (const [id, ref] of cards) {
      const show = (!categoryFilter || ref.def.category === categoryFilter) && (!statusFilter || statusOf(id) === statusFilter);
      ref.card.hidden = !show;
      if (show) visibleByCategory.set(ref.def.category, (visibleByCategory.get(ref.def.category) || 0) + 1);
    }
    for (const [category, heading] of headings) {
      const count = visibleByCategory.get(category) || 0;
      heading.hidden = count === 0;
      const total = catalog.filter((d) => d.category === category).length;
      const passed = catalog.filter((d) => d.category === category && statusOf(d.id) === 'pass').length;
      setText(heading.querySelector('[data-count]'), `${passed}/${total} passed`);
    }
    for (const button of dom['scn-category-pills'].children) {
      const active = button.dataset.category === categoryFilter;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    }
  }

  function renderSummary() {
    const counts = { pass: 0, fail: 0, inconclusive: 0, notRun: 0 };
    for (const def of catalog) counts[statusOf(def.id)] = (counts[statusOf(def.id)] || 0) + 1;
    const total = catalog.length;

    const host = clear(dom['scn-summary']);
    host.append(TP.ui.pill(`${total} scenarios`, 'muted'));
    for (const s of STATUSES) host.append(TP.ui.pill(`${s.label}: ${counts[s.value] || 0}`, s.tone));

    const pct = (n) => (total ? `${(n / total) * 100}%` : '0%');
    dom['scn-bar-pass'].style.width = pct(counts.pass);
    dom['scn-bar-fail'].style.width = pct(counts.fail);
    dom['scn-bar-inconclusive'].style.width = pct(counts.inconclusive);
  }

  function showCategory(category) {
    categoryFilter = category || '';
    dom['scn-status-filter'].value = '';
    applyFilters();
  }

  async function loadResults() {
    const r = await run('scenarios.results', [], { toast: false });
    if (!r.ok || !Array.isArray(r.value)) return;
    results.clear();
    for (const item of r.value) results.set(item.scenarioId, item);
    for (const id of cards.keys()) {
      const ref = cards.get(id);
      ref.notes.value = '';
      updateCard(id);
    }
    renderSummary();
    applyFilters();
  }

  async function resetAll() {
    const ok = await TP.ui.confirm({
      title: 'Reset all results?',
      message: 'Every recorded verdict and note will be cleared and all scenarios return to Not run. This cannot be undone.',
      confirmText: 'Reset results',
      danger: true,
    });
    if (!ok) return;
    const r = await run('scenarios.reset', [], { label: 'Reset' });
    if (r.ok) {
      await loadResults();
      toast('All scenario results cleared.', 'info');
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Soak
  // ---------------------------------------------------------------------------------------------

  function currentElapsed(soak) {
    if (!soak || !soak.startedAtUtc) return null;
    return soak.active ? soak.elapsedSeconds + (performance.now() - soakReceivedAt) / 1000 : soak.elapsedSeconds;
  }

  function renderSoakElapsed() {
    setText(dom['soak-elapsed'], fmt.elapsedSeconds(currentElapsed(state.soak)));
  }

  function renderSoak() {
    const soak = state.soak;
    const badge = dom['soak-state'];
    const active = Boolean(soak && soak.active);
    const started = Boolean(soak && soak.startedAtUtc);

    setText(badge, active ? 'Running' : started ? 'Stopped' : 'Not started');
    badge.className = `badge ${active ? 'text-bg-success' : 'text-bg-secondary'}`;
    dom['btn-soak-start'].disabled = active;
    dom['btn-soak-stop'].disabled = !active;

    renderSoakElapsed();
    setText(dom['soak-started'], started ? new Date(soak.startedAtUtc).toLocaleString() : '—');
    setText(dom['soak-errors'], started ? String(soak.errorCount) : '—');
    setText(dom['soak-underruns'], started ? String(soak.underrunCount) : '—');
    setText(dom['soak-ws-start'], started ? fmt.bytes(soak.startWorkingSetBytes) : '—');
    setText(dom['soak-ws-latest'], started ? fmt.bytes(soak.latestWorkingSetBytes) : '—');
    setText(dom['soak-ws-peak'], started ? fmt.bytes(soak.peakWorkingSetBytes) : '—');
    setText(
      dom['soak-ws-delta'],
      started && typeof soak.latestWorkingSetBytes === 'number' && typeof soak.startWorkingSetBytes === 'number'
        ? fmt.signedBytes(soak.latestWorkingSetBytes - soak.startWorkingSetBytes)
        : '—',
    );

    const notes = Array.isArray(soak && soak.notes) ? soak.notes : [];
    const host = dom['soak-notes'];
    if (host.childElementCount !== notes.length) {
      clear(host);
      for (const note of notes) host.append(el('li', { text: note }));
      host.scrollTop = host.scrollHeight;
    }

    if (active && !soakTimer) soakTimer = setInterval(() => {
      if (state.section === 'scenarios') renderSoakElapsed();
    }, SOAK_TICK_MS);
    if (!active && soakTimer) {
      clearInterval(soakTimer);
      soakTimer = null;
    }
  }

  function applySoak(soak) {
    soakReceivedAt = performance.now();
    TP.core.setSoak(soak);
  }

  async function soakCommand(name, args, label) {
    const r = await run(name, args, { label });
    if (r.ok && r.value) applySoak(r.value);
    return r;
  }

  function wireSoak() {
    dom['btn-soak-start'].addEventListener('click', async () => {
      const r = await soakCommand('soak.start', [], 'Soak');
      if (r.ok) toast('Soak session started. Error and underrun counts are tracked from now.', 'info');
    });
    dom['btn-soak-stop'].addEventListener('click', async () => {
      const r = await soakCommand('soak.stop', [], 'Soak');
      if (r.ok) toast('Soak session stopped. Review the numbers and record your own verdict.', 'info');
    });
    const addNote = async () => {
      const text = dom['soak-note-input'].value.trim();
      if (!text) return toast('Type a note first.', 'warning');
      const r = await soakCommand('soak.note', [text], 'Soak note');
      if (r.ok) dom['soak-note-input'].value = '';
    };
    dom['btn-soak-note'].addEventListener('click', addNote);
    dom['soak-note-input'].addEventListener('keydown', (e) => {
      if (e.key === 'Enter') addNote();
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------------------------

  async function init() {
    resolveDom();
    wireSoak();
    dom['scn-status-filter'].addEventListener('change', applyFilters);
    dom['btn-scn-reset'].addEventListener('click', resetAll);

    TP.bus.on('resources', () => {
      if (state.soak) soakReceivedAt = performance.now();
      renderSoak();
    });
    TP.bus.on('soak', renderSoak);
    TP.bus.on('section', (name) => {
      if (name === 'scenarios') renderSoak();
    });

    const [catalogResult, soakResult] = await Promise.all([
      run('scenarios.catalog', [], { label: 'Scenario catalog' }),
      run('soak.get', [], { toast: false }),
    ]);
    if (catalogResult.ok && Array.isArray(catalogResult.value)) {
      catalog = catalogResult.value;
      buildCards();
    } else {
      clear(dom['scn-list']).append(el('div', { class: 'empty-state' }, el('div', { text: 'The scenario catalog could not be loaded.' })));
    }
    if (soakResult.ok && soakResult.value) applySoak(soakResult.value);
    await loadResults();
    renderSummary();
    applyFilters();
    renderSoak();
  }

  TP.scenarios = { init, showCategory };
})();
