/*
 * output.js — output device list, output configuration form, requested-vs-effective state, manual checklist.
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { el, clear, setText, setPill, toast } = TP.ui;
  const { run } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;
  const logic = TP.outputFormLogic;

  const MIN_LATENCY_MS = 5;
  const MAX_LATENCY_MS = 500;

  const dom = {};
  let formDirty = false;
  let configureInFlight = false;
  /** Last saved requested configuration (engine `OutputConfiguration`), not effective mode. */
  let savedRequest = logic.requestedFromOutput(null);
  let switchingId; // undefined = idle, null = switching to system default, string = switching to device id

  const DEVICE_STATE_TONE = { active: 'ok', disabled: 'warn', unplugged: 'warn', notPresent: 'bad', unknown: 'muted' };
  const OUTPUT_STATUS_TONE = { available: 'ok', switching: 'info', preferredUnavailable: 'warn', unavailable: 'bad' };

  function resolveDom() {
    const ids = [
      'btn-refresh-devices', 'device-list', 'device-empty', 'output-form', 'out-mode', 'out-fallback', 'out-latency',
      'out-latency-default', 'btn-apply-output', 'out-form-error', 'eff-fallback-banner', 'eff-req-mode', 'eff-mode',
      'eff-fallback', 'eff-status', 'eff-preferred', 'eff-active', 'eff-recovery', 'eff-req-latency', 'eff-latency',
      'output-checklist',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  // ---------------------------------------------------------------------------------------------
  // Name resolution (shared with the playback bar)
  // ---------------------------------------------------------------------------------------------

  function deviceName(id) {
    if (!id) return null;
    const found = state.devices.find((d) => d.id === id);
    return found ? found.name : null;
  }

  /** Short label for the device the player is bound to (or would use). */
  function activeLabel() {
    const out = state.output;
    if (!out) return '—';
    if (out.activeDeviceId) {
      const name = deviceName(out.activeDeviceId) || 'Unknown device';
      return out.deviceStatus && out.deviceStatus !== 'available' ? `${name} (${fmt.humanize(out.deviceStatus).toLowerCase()})` : name;
    }
    if (out.preferredDeviceId) {
      return `${deviceName(out.preferredDeviceId) || 'Preferred device'} (${fmt.humanize(out.deviceStatus || 'unavailable').toLowerCase()})`;
    }
    const def = state.devices.find((d) => d.isDefault);
    return def ? `${def.name} (system default)` : 'No device attached';
  }

  // ---------------------------------------------------------------------------------------------
  // Devices
  // ---------------------------------------------------------------------------------------------

  function renderDevices() {
    const list = clear(dom['device-list']);
    const out = state.output;
    const devices = state.devices;
    dom['device-empty'].hidden = devices.length > 0;

    const followingDefault = !out || !out.preferredDeviceId;

    const makeRow = ({ id, name, sub, badges, active, unavailable, canUse }) => {
      const busy = switchingId !== undefined && switchingId === id;
      const useButton = el(
        'button',
        { type: 'button', class: `btn btn-sm ${active ? 'btn-outline-secondary' : 'btn-accent'}`, disabled: !canUse || switchingId !== undefined || active },
        busy ? el('span', { class: 'spinner-border spinner-border-sm', 'aria-hidden': 'true' }) : null,
        busy ? ' Switching' : active ? 'In use' : 'Use',
      );
      useButton.addEventListener('click', () => selectDevice(id));
      return el(
        'li',
        { class: `device-item${active ? ' is-active' : ''}${unavailable ? ' is-unavailable' : ''}` },
        el('i', { class: `bi ${id === null ? 'bi-speaker' : 'bi-volume-up'} device-icon`, 'aria-hidden': 'true' }),
        el('div', { class: 'device-name' }, el('div', { class: 'name', text: name, title: name }), sub ? el('div', { class: 'sub', text: sub, title: sub }) : null),
        el('div', { class: 'device-badges' }, badges),
        useButton,
      );
    };

    const defaultDevice = devices.find((d) => d.isDefault);
    list.append(
      makeRow({
        id: null,
        name: 'System default',
        sub: defaultDevice ? `Currently: ${defaultDevice.name}` : 'Follows the Windows default render device',
        badges: followingDefault ? [TP.ui.pill('Preferred', 'accent')] : [],
        active: followingDefault,
        unavailable: false,
        canUse: true,
      }),
    );

    for (const device of devices) {
      const badges = [];
      if (device.isDefault) badges.push(TP.ui.pill('Default', 'info'));
      if (device.isPreferred) badges.push(TP.ui.pill('Preferred', 'accent'));
      if (device.isActive) badges.push(TP.ui.pill('Active', 'ok'));
      if (device.state && device.state !== 'active') badges.push(TP.ui.pill(fmt.humanize(device.state), DEVICE_STATE_TONE[device.state] || 'muted'));
      list.append(
        makeRow({
          id: device.id,
          name: device.name,
          sub: device.id,
          badges,
          active: device.isPreferred,
          unavailable: device.state !== 'active',
          canUse: device.state === 'active',
        }),
      );
    }
  }

  async function selectDevice(id) {
    switchingId = id;
    renderDevices();
    const result = await run('devices.set', [id], { label: 'Device switch' });
    switchingId = undefined;
    if (result.ok) toast(id === null ? 'Following the system default device.' : `Selected ${deviceName(id) || 'device'}.`, 'success');
    await refresh({ quiet: true });
    await TP.core.refreshSnapshot();
  }

  // ---------------------------------------------------------------------------------------------
  // Effective state
  // ---------------------------------------------------------------------------------------------

  function renderEffective() {
    const out = state.output;
    if (!out) return;

    setPill(dom['eff-req-mode'], fmt.humanize(out.mode), 'accent');
    if (out.effectiveMode) setPill(dom['eff-mode'], fmt.humanize(out.effectiveMode), out.effectiveMode === out.mode ? 'ok' : 'warn');
    else setPill(dom['eff-mode'], 'No output attached', 'muted');

    setPill(dom['eff-fallback'], fmt.humanize(out.fallbackReason || 'none'), out.fallbackReason && out.fallbackReason !== 'none' ? 'warn' : 'muted');
    setPill(dom['eff-status'], fmt.humanize(out.deviceStatus), OUTPUT_STATUS_TONE[out.deviceStatus] || 'muted');
    setText(dom['eff-preferred'], out.preferredDeviceId ? deviceName(out.preferredDeviceId) || out.preferredDeviceId : 'System default');
    setText(dom['eff-active'], out.activeDeviceId ? deviceName(out.activeDeviceId) || out.activeDeviceId : 'None');
    setText(dom['eff-recovery'], fmt.humanize(out.recoveryIntent));
    setText(dom['eff-req-latency'], out.requestedLatencyMs == null ? 'Backend default' : `${Math.round(out.requestedLatencyMs)} ms`);
    if (out.effectiveLatencyMs == null) {
      setPill(dom['eff-latency'], 'Unavailable', 'muted');
    } else {
      dom['eff-latency'].classList.remove('tp-pill');
      delete dom['eff-latency'].dataset.tone;
      setText(dom['eff-latency'], `${out.effectiveLatencyMs.toFixed(1)} ms`);
    }

    const banner = dom['eff-fallback-banner'];
    const fellBack = out.effectiveMode && out.effectiveMode !== out.mode;
    banner.hidden = !fellBack;
    if (fellBack) {
      setText(banner, `Requested ${fmt.humanize(out.mode).toLowerCase()} mode, but the output is running in ${fmt.humanize(out.effectiveMode).toLowerCase()} mode (reason: ${fmt.humanize(out.fallbackReason || 'none').toLowerCase()}).`);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Configuration form
  // ---------------------------------------------------------------------------------------------

  function readFormRequest() {
    return logic.requestedFromForm({
      mode: dom['out-mode'].value,
      exclusiveFallback: dom['out-fallback'].value,
      useDefaultLatency: dom['out-latency-default'].checked,
      latencyMs: dom['out-latency'].value,
    });
  }

  function syncLatencyInput() {
    const useDefault = dom['out-latency-default'].checked;
    dom['out-latency'].disabled = useDefault;
  }

  function syncFallbackEnabled() {
    dom['out-fallback'].disabled = dom['out-mode'].value !== 'exclusive';
  }

  function updateApplyEnabled() {
    const button = dom['btn-apply-output'];
    if (!button) return;
    const enable = logic.shouldEnableApply({
      formRequest: readFormRequest(),
      savedRequest,
      configureInFlight,
    });
    button.disabled = !enable;
    button.title = configureInFlight
      ? 'Configuration change in progress…'
      : enable
        ? 'Apply the selected output configuration'
        : 'No pending configuration changes';
  }

  function markFormDirty() {
    formDirty = true;
    syncFallbackEnabled();
    updateApplyEnabled();
  }

  function applyFormValues(values) {
    dom['out-mode'].value = values.mode;
    dom['out-fallback'].value = values.exclusiveFallback;
    dom['out-latency-default'].checked = values.useDefaultLatency;
    dom['out-latency'].value = String(values.latencyMs);
    syncLatencyInput();
    syncFallbackEnabled();
  }

  /**
   * Sync editable controls from engine requested configuration only.
   * Never overwrites pending edits; ignores effective-only output updates.
   */
  function syncForm(incomingOutput) {
    const out = incomingOutput || state.output;
    if (!out) return;

    if (!logic.shouldSyncFormFromOutput({ formDirty, savedRequest, incomingOutput: out })) {
      updateApplyEnabled();
      return;
    }

    savedRequest = logic.requestedFromOutput(out);
    applyFormValues(logic.formValuesFromRequest(savedRequest));
    formDirty = false;
    updateApplyEnabled();
  }

  function showFormError(message) {
    dom['out-form-error'].hidden = !message;
    setText(dom['out-form-error'], message || '');
  }

  async function applyConfiguration(event) {
    event.preventDefault();
    if (configureInFlight) {
      toast('An output configuration change is already in progress.', 'info');
      return;
    }

    if (!logic.shouldEnableApply({ formRequest: readFormRequest(), savedRequest, configureInFlight: false })) {
      toast('No output configuration changes to apply.', 'info');
      updateApplyEnabled();
      return;
    }

    showFormError('');
    let latency = null;
    if (!dom['out-latency-default'].checked) {
      latency = Number(dom['out-latency'].value);
      if (!Number.isFinite(latency) || latency < MIN_LATENCY_MS || latency > MAX_LATENCY_MS) {
        showFormError(`Latency must be between ${MIN_LATENCY_MS} and ${MAX_LATENCY_MS} ms.`);
        dom['out-latency'].focus();
        return;
      }
    }

    const request = {
      mode: logic.normalizeMode(dom['out-mode'].value),
      exclusiveFallback: logic.normalizeFallback(dom['out-fallback'].value),
      requestedLatencyMs: latency,
    };

    configureInFlight = true;
    updateApplyEnabled();
    try {
      const result = await run('output.configure', [request], { label: 'Output configuration', toast: false });
      // Always resync host state after success or failure; do not clobber a still-dirty retry form
      // until we know whether the request stuck.
      await refresh({ quiet: true });
      await TP.core.refreshSnapshot();

      if (result.ok) {
        if (result.value) TP.core.setOutput(result.value);
        savedRequest = logic.requestedFromOutput(result.value || state.output);
        formDirty = false;
        applyFormValues(logic.formValuesFromRequest(savedRequest));
        const out = state.output || result.value;
        const eff = out && out.effectiveMode;
        const requested = out && out.mode;
        const hasSource = out && out.hasSource;
        if (eff) {
          toast(`Output configured. Effective mode: ${fmt.humanize(eff)}.`, 'success');
        } else if (!hasSource) {
          toast(
            `Output preference saved (${fmt.humanize(requested || 'shared')}). It will apply when a track is loaded.`,
            'success');
        } else if (requested === 'exclusive') {
          toast('Exclusive mode was requested but no output is attached. Check Diagnostics for details.', 'warning');
        } else {
          toast('Output configured (no output attached yet).', 'success');
        }
      } else {
        const message = result.message || 'Output configuration failed.';
        showFormError(message);
        toast(`Output configuration: ${message}`, 'danger');
        // Keep the user's attempted values so they can edit and retry.
        formDirty = true;
      }
    } finally {
      configureInFlight = false;
      updateApplyEnabled();
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Refresh / init
  // ---------------------------------------------------------------------------------------------

  async function refresh({ quiet = false } = {}) {
    const [devices, output] = await Promise.all([
      run('devices.list', [], { toast: !quiet, label: 'Devices' }),
      run('output.get', [], { toast: !quiet, label: 'Output state' }),
    ]);
    if (devices.ok) TP.core.setDevices(devices.value);
    if (output.ok) TP.core.setOutput(output.value);
  }

  const CHECKLIST = [
    'Switch device during playback: audio resumes on the new device, position is preserved, status returns to Available.',
    'Select a USB or Bluetooth device and play, then unplug it: status shows Preferred unavailable / Unavailable.',
    'Reconnect the device: output recovers according to the recovery intent (resume or remain paused).',
    'Request Exclusive with fallback None: effective mode is Exclusive, or a classified failure appears. It must not silently use Shared.',
    'Hold the device in another exclusive app, then request Exclusive with fallback Shared: effective mode is Shared and a fallback reason is shown.',
    'Change the Windows default device while following “System default”: the output follows it.',
    'Request a latency (e.g. 20 ms and 200 ms): configuration is accepted and effective latency is reported or shown as unavailable.',
    'After Exclusive applies, change Share mode back to Shared and Apply: the switch succeeds in both directions.',
  ];

  function render() {
    renderDevices();
    renderEffective();
    // Only rewrite editable controls when the *requested* config changes and the form is clean.
    syncForm(state.output);
  }

  function init() {
    resolveDom();
    TP.ui.checklist(dom['output-checklist'], 'output', CHECKLIST);

    dom['btn-refresh-devices'].addEventListener('click', () => refresh());
    dom['output-form'].addEventListener('submit', applyConfiguration);
    dom['out-latency-default'].addEventListener('change', markFormDirty);
    // input fires as soon as the selection changes; change alone can lose to a same-tick syncForm.
    dom['out-mode'].addEventListener('input', markFormDirty);
    dom['out-mode'].addEventListener('change', markFormDirty);
    dom['out-fallback'].addEventListener('input', markFormDirty);
    dom['out-fallback'].addEventListener('change', markFormDirty);
    dom['out-latency'].addEventListener('input', () => {
      showFormError('');
      markFormDirty();
    });

    TP.bus.on('devices', () => {
      renderDevices();
    });
    TP.bus.on('output', (payload) => {
      renderEffective();
      renderDevices();
      syncForm(payload || state.output);
    });

    updateApplyEnabled();
    return refresh({ quiet: true });
  }

  TP.output = { init, refresh, deviceName, activeLabel };
})();
