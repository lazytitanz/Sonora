/*
 * app.js — navigation, startup sequence, host event subscriptions and the shared state setters (TP.core).
 *
 * Modules never talk to vetra.on directly: every host event lands here, updates TP.state, and is re-published on
 * TP.bus so each module can react without knowing about the bridge.
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { $, $$, setText, toast } = TP.ui;
  const { run, whenVetra, on, invoke, errorText } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;
  const bus = TP.bus;
  const E = TP.EVENTS;

  const SECTIONS = ['player', 'output', 'dsp', 'streaming', 'diagnostics', 'scenarios'];
  const SECTION_STORAGE_KEY = 'tp.section';
  const VISIBILITY_REFRESH_MIN_MS = 1000;

  let lastVisibilityRefresh = 0;

  // ---------------------------------------------------------------------------------------------
  // Navigation
  // ---------------------------------------------------------------------------------------------

  function go(name) {
    const target = SECTIONS.includes(name) ? name : 'player';
    state.section = target;
    for (const section of SECTIONS) {
      const panel = document.getElementById(`section-${section}`);
      if (panel) panel.hidden = section !== target;
    }
    for (const button of $$('.nav-btn')) {
      const active = button.dataset.section === target;
      button.classList.toggle('active', active);
      if (active) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    }
    try {
      localStorage.setItem(SECTION_STORAGE_KEY, target);
    } catch {
      /* storage unavailable: navigation still works */
    }
    $('#app-main').scrollTop = 0;
    bus.emit('section', target);
  }

  function wireNavigation() {
    for (const button of $$('.nav-btn')) {
      button.addEventListener('click', () => go(button.dataset.section));
    }
    document.addEventListener('keydown', (event) => {
      if (!event.altKey || event.ctrlKey || event.metaKey) return;
      const index = Number(event.key) - 1;
      if (Number.isInteger(index) && index >= 0 && index < SECTIONS.length) {
        event.preventDefault();
        go(SECTIONS[index]);
      }
    });
    let initial = 'player';
    try {
      initial = localStorage.getItem(SECTION_STORAGE_KEY) || 'player';
    } catch {
      initial = 'player';
    }
    go(initial);
  }

  // ---------------------------------------------------------------------------------------------
  // Header / engine status
  // ---------------------------------------------------------------------------------------------

  function renderEngineStatus() {
    const pill = $('#engine-status');
    const label = $('#engine-status-text');
    const s = state.snapshot;
    let status = 'connecting';
    let text = 'Connecting…';
    let title = 'Waiting for the host';

    if (s) {
      const out = state.output;
      if (s.disposed) {
        status = 'error';
        text = 'Engine disposed';
        title = 'The playback controller has been disposed.';
      } else if (!s.engineCreated) {
        status = 'warn';
        text = 'No engine';
        title = 'The audio engine has not been created.';
      } else if (out && (out.deviceStatus === 'unavailable' || out.deviceStatus === 'preferredUnavailable')) {
        status = 'warn';
        text = `Engine ready · output ${fmt.humanize(out.deviceStatus).toLowerCase()}`;
        title = 'The engine is running but the output device is not available.';
      } else if (out && out.deviceStatus === 'switching') {
        status = 'warn';
        text = 'Engine ready · switching output';
        title = 'The output device is being switched.';
      } else {
        status = 'ok';
        text = 'Engine ready';
        title = 'Engine and player created.';
      }
    }

    pill.dataset.status = status;
    pill.title = title;
    setText(label, text);
  }

  function renderVersion() {
    const env = state.appInfo && state.appInfo.environment;
    const badge = $('#app-version');
    if (!env || !env.testPlayerVersion) return;
    const short = String(env.testPlayerVersion).split('+')[0];
    setText(badge, `v${short}`);
    badge.title = `Test Player ${env.testPlayerVersion}\nSonora ${env.sonoraVersion || 'unknown'}`;
  }

  // ---------------------------------------------------------------------------------------------
  // State setters (also exposed as TP.core)
  // ---------------------------------------------------------------------------------------------

  const sameJson = (a, b) => {
    try {
      return JSON.stringify(a) === JSON.stringify(b);
    } catch {
      return false;
    }
  };

  function applySnapshot(snapshot) {
    if (!snapshot) return;
    state.snapshot = snapshot;
    if (snapshot.output) setOutput(snapshot.output);
    renderEngineStatus();
    bus.emit('snapshot', snapshot);
  }

  function mergeIntoSnapshot(partial) {
    if (!state.snapshot) return false;
    state.snapshot = { ...state.snapshot, ...partial };
    return true;
  }

  function setOutput(output) {
    if (!output || sameJson(state.output, output)) return;
    state.output = output;
    renderEngineStatus();
    bus.emit('output', output);
  }

  function setDevices(devices) {
    if (!Array.isArray(devices) || sameJson(state.devices, devices)) return;
    state.devices = devices;
    bus.emit('devices', devices);
  }

  function setQueue(queue) {
    if (!Array.isArray(queue)) return;
    state.queue = queue;
    bus.emit('queue', queue);
  }

  function setDsp(dsp) {
    if (!dsp) return;
    state.dsp = dsp;
    bus.emit('dsp', dsp);
  }

  function setStreamStatus(status) {
    if (!status) return;
    state.streamStatus = status;
    bus.emit('streamStatus', status);
  }

  function setSoak(soak) {
    if (!soak) return;
    state.soak = soak;
    bus.emit('soak', soak);
  }

  function setResources(sample, soak) {
    if (sample) state.resources = sample;
    if (soak) state.soak = soak;
    bus.emit('resources', { sample, soak });
  }

  async function refreshSnapshot() {
    const result = await run('playback.getSnapshot', [], { toast: false });
    if (result.ok) applySnapshot(result.value);
    return result.ok;
  }

  async function refreshQueue() {
    const result = await run('queue.get', [], { toast: false });
    if (result.ok) setQueue(result.value);
    return result.ok;
  }

  async function refreshAll() {
    await Promise.allSettled([
      refreshSnapshot(),
      refreshQueue(),
      TP.output.refresh({ quiet: true }),
      TP.dsp.refresh({ quiet: true }),
      TP.streaming.refresh({ quiet: true }),
    ]);
  }

  TP.core = {
    go,
    applySnapshot,
    mergeIntoSnapshot,
    refreshSnapshot,
    refreshQueue,
    refreshAll,
    setOutput,
    setDevices,
    setQueue,
    setDsp,
    setStreamStatus,
    setSoak,
    setResources,
  };

  // ---------------------------------------------------------------------------------------------
  // Host events
  // ---------------------------------------------------------------------------------------------

  function subscribeEvents() {
    on(E.snapshot, (payload) => applySnapshot(payload));

    on(E.position, (payload) => {
      if (!payload || !mergeIntoSnapshot(payload)) return;
      bus.emit('position', payload);
    });

    on(E.trackChanged, (payload) => {
      state.currentItem = payload || null;
      bus.emit('trackChanged', payload);
    });

    on(E.completed, (payload) => {
      bus.emit('completed', payload);
      refreshSnapshot();
    });

    on(E.queueChanged, (payload) => setQueue(payload));

    on(E.diagnosticEvent, (payload) => bus.emit('diagEvent', payload));

    on(E.stall, (payload) => {
      const msg = (payload && payload.message) || 'Playback stalled while Playing (no audio progress).';
      toast(msg, 'danger');
      bus.emit('stall', payload);
    });

    on(E.resources, (payload) => {
      if (payload) setResources(payload.sample, payload.soak);
    });

    on(E.devicesChanged, (payload) => {
      if (payload && Array.isArray(payload.devices)) setDevices(payload.devices);
      else TP.output.refresh({ quiet: true });
    });

    on(E.outputChanged, (payload) => setOutput(payload));

    on(E.networkChanged, (payload) => {
      if (payload && mergeIntoSnapshot({ networkStatus: payload.status, networkBytesBuffered: payload.bytesBuffered })) {
        bus.emit('network', payload);
      }
    });

    on(E.error, (payload) => {
      const message = payload && payload.message ? payload.message : 'Unknown playback error';
      const source = payload && payload.source ? ` (${payload.source})` : '';
      toast(`Playback error${source}: ${message}`, 'danger');
      bus.emit('error', payload);
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Startup
  // ---------------------------------------------------------------------------------------------

  async function fetchHostInfo() {
    const [info, caps] = await Promise.all([
      run('app.getInfo', [], { label: 'App info' }),
      invoke('vetra.app.getCapabilities').then(
        (value) => ({ ok: true, value }),
        () => ({ ok: false }),
      ),
    ]);
    if (info.ok) {
      state.appInfo = info.value;
      if (info.value && typeof info.value.musicFolder === 'string' && info.value.musicFolder.length > 0) {
        state.musicFolder = info.value.musicFolder;
      }
    }
    // If capabilities cannot be read, assume dialogs work; a failing dialog reports its own error.
    state.capabilities = caps.ok ? caps.value : null;
    state.dialogsEnabled = caps.ok ? Boolean(caps.value && caps.value.dialogs) : true;
    renderVersion();
    bus.emit('appInfo', state.appInfo);
    bus.emit('capabilities', state.capabilities);
  }

  async function startModule(name, start) {
    try {
      await start();
    } catch (error) {
      console.error(`${name} failed to start:`, error);
      toast(`${name} failed to start: ${errorText(error)}`, 'danger');
    }
  }

  async function init() {
    wireNavigation();
    renderEngineStatus();

    $('#btn-refresh-all').addEventListener('click', async () => {
      await refreshAll();
      toast('State refreshed.', 'info');
    });

    document.addEventListener('visibilitychange', () => {
      const now = performance.now();
      if (!document.hidden && now - lastVisibilityRefresh > VISIBILITY_REFRESH_MIN_MS) {
        lastVisibilityRefresh = now;
        refreshAll();
      }
    });

    window.addEventListener('unhandledrejection', (event) => {
      console.error('Unhandled rejection:', event.reason);
      toast(`Unexpected error: ${errorText(event.reason)}`, 'danger');
    });

    await whenVetra();
    subscribeEvents();
    await fetchHostInfo();

    await startModule('Player', () => TP.player.init());
    await Promise.all([
      startModule('Playback state', async () => {
        await refreshSnapshot();
        await refreshQueue();
      }),
      startModule('Audio output', () => TP.output.init()),
      startModule('DSP', () => TP.dsp.init()),
      startModule('Streaming', () => TP.streaming.init()),
      startModule('Diagnostics', () => TP.diagnostics.init()),
      startModule('Scenarios', () => TP.scenarios.init()),
    ]);

    renderEngineStatus();
  }

  init().catch((error) => {
    console.error('Startup failed:', error);
    const pill = $('#engine-status');
    pill.dataset.status = 'error';
    setText($('#engine-status-text'), 'Startup failed');
    pill.title = errorText(error);
    toast(`Startup failed: ${errorText(error)}`, 'danger');
  });
})();
