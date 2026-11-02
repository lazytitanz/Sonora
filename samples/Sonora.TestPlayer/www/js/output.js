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

  if (!logic) {
    console.error('TP.outputFormLogic missing — outputFormLogic.js did not load. Check WebRoot / script tags.');
  }

  const MIN_LATENCY_MS = 5;
  const MAX_LATENCY_MS = 500;

  const dom = {};
  const session = logic.createFormSession(null);
  let switchingId; // undefined = idle, null = switching to system default, string = switching to device id
  let lastDomModeBeforeSnapshot = null;
  let snapshotClobberCount = 0;
  let applyClickCount = 0;

  const DEVICE_STATE_TONE = { active: 'ok', disabled: 'warn', unplugged: 'warn', notPresent: 'bad', unknown: 'muted' };
  const OUTPUT_STATUS_TONE = { available: 'ok', switching: 'info', preferredUnavailable: 'warn', unavailable: 'bad' };

  function resolveDom() {
    const ids = [
      'btn-refresh-devices', 'device-list', 'device-empty', 'output-form', 'out-mode', 'out-fallback', 'out-latency',
      'out-latency-default', 'btn-apply-output', 'out-form-error', 'out-apply-debug', 'eff-fallback-banner', 'eff-req-mode', 'eff-mode',
      'eff-fallback', 'eff-status', 'eff-preferred', 'eff-active', 'eff-recovery', 'eff-req-latency', 'eff-latency',
      'output-checklist',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  function deviceName(id) {
    if (!id) return null;
    const found = state.devices.find((d) => d.id === id);
    return found ? found.name : null;
  }

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

  function clearDeviceSwitchBusy() {
    if (switchingId === undefined) return;
    switchingId = undefined;
    renderDevices();
  }

  /** True when the host output snapshot matches the in-flight Use click target. */
  function outputMatchesSwitchTarget(out, targetId) {
    if (!out || targetId === undefined) return false;
    const preferred = out.preferredDeviceId == null ? null : String(out.preferredDeviceId);
    const target = targetId == null ? null : String(targetId);
    return preferred === target;
  }

  async function selectDevice(id) {
    switchingId = id;
    renderDevices();
    try {
      const result = await run('devices.set', [id], { label: 'Device switch' });
      if (result.ok) {
        toast(id === null ? 'Following the system default device.' : `Selected ${deviceName(id) || 'device'}.`, 'success');
      }
    } finally {
      // Always re-render after clearing: refresh may no-op when output events already
      // applied the settled Available snapshot while switchingId was still set.
      clearDeviceSwitchBusy();
    }
    await refresh({ quiet: true });
    await TP.core.refreshSnapshot();
  }

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

  function readDomForm() {
    return logic.requestedFromForm({
      mode: dom['out-mode'].value,
      exclusiveFallback: dom['out-fallback'].value,
      useDefaultLatency: dom['out-latency-default'].checked,
      latencyMs: dom['out-latency'].value,
    });
  }

  function syncLatencyInput() {
    dom['out-latency'].disabled = dom['out-latency-default'].checked;
  }

  function syncFallbackEnabled() {
    dom['out-fallback'].disabled = dom['out-mode'].value !== 'exclusive';
  }

  function applyFormValues(values) {
    dom['out-mode'].value = values.mode;
    dom['out-fallback'].value = values.exclusiveFallback;
    dom['out-latency-default'].checked = values.useDefaultLatency;
    dom['out-latency'].value = String(values.latencyMs);
    syncLatencyInput();
    syncFallbackEnabled();
  }

  function paintDebug(extra = {}) {
    const snap = session.snapshot();
    const form = readDomForm();
    const button = dom['btn-apply-output'];
    const panel = dom['out-apply-debug'];
    const rect = button ? button.getBoundingClientRect() : null;
    const style = button ? window.getComputedStyle(button) : null;
    const lines = [
      `asset=${logic.ASSET_BUILD_ID}`,
      `domMode=${dom['out-mode'] && dom['out-mode'].value}  formDirty=${snap.formDirty}  inFlight=${snap.configureInFlight}`,
      `pending=${snap.pendingRequest ? JSON.stringify(snap.pendingRequest) : 'null'}`,
      `saved=${JSON.stringify(snap.savedRequest)}`,
      `form =${JSON.stringify(form)}`,
      `equal=${logic.requestsEqual(form, snap.savedRequest)}  applyEnabled=${logic.shouldEnableApply({ formRequest: form, savedRequest: snap.savedRequest, configureInFlight: snap.configureInFlight })}`,
      `disabledReason=${logic.applyDisabledReason({ formRequest: form, savedRequest: snap.savedRequest, configureInFlight: snap.configureInFlight }) || 'none'}`,
      `button.disabled=${button ? button.disabled : 'n/a'}  pointer-events=${style ? style.pointerEvents : 'n/a'}`,
      `clicks=${applyClickCount}  snapshotClobbers=${snapshotClobberCount}  reconcile=${snap.lastReconcile || 'none'}`,
      `lastClick=${snap.lastClick ? JSON.stringify(snap.lastClick) : 'none'}`,
      rect ? `btnBox=${Math.round(rect.left)},${Math.round(rect.top)} ${Math.round(rect.width)}x${Math.round(rect.height)}` : 'btnBox=n/a',
    ];
    for (const [k, v] of Object.entries(extra)) lines.push(`${k}=${v}`);
    const text = lines.join('\n');
    if (panel) setText(panel, text);
    window.__TP_OUTPUT_DEBUG__ = {
      ...snap,
      domMode: dom['out-mode'] && dom['out-mode'].value,
      domForm: form,
      buttonDisabled: button ? button.disabled : null,
      pointerEvents: style ? style.pointerEvents : null,
      applyClickCount,
      snapshotClobberCount,
      webRootHint: document.baseURI,
      scripts: Array.from(document.scripts).map((s) => s.src).filter((s) => s.includes('output')),
      extra,
    };
  }

  function toastConfigureResult(out) {
    // Mid exclusive↔shared rebind: EffectiveMode is briefly null while music continues.
    // Do not warn until the host reports a settled snapshot.
    if (logic.isOutputSwitching(out)) {
      return;
    }
    const eff = out && out.effectiveMode;
    const requested = out && out.mode;
    const hasSource = out && out.hasSource;
    if (eff) {
      toast(`Output configured. Effective mode: ${fmt.humanize(eff)}.`, 'success');
    } else if (!hasSource) {
      toast(
        `Output preference saved (${fmt.humanize(requested || 'shared')}). It will apply when a track is loaded.`,
        'success');
    } else if (logic.normalizeMode(requested) === 'exclusive') {
      toast('Exclusive mode was requested but no output is attached. Check Diagnostics for details.', 'warning');
    } else {
      toast('Output configured (no output attached yet).', 'success');
    }
  }

  function updateApplyEnabled() {
    const button = dom['btn-apply-output'];
    if (!button) return;
    const form = readDomForm();
    const snap = session.snapshot();
    // Keep session formRequest aligned with the live DOM after user edits.
    const enable = logic.shouldEnableApply({
      formRequest: form,
      savedRequest: snap.savedRequest,
      configureInFlight: snap.configureInFlight,
    });
    button.disabled = !enable;
    button.title = snap.configureInFlight
      ? 'Configuration change in progress…'
      : enable
        ? 'Apply the selected output configuration'
        : `Nothing to apply (${logic.applyDisabledReason({ formRequest: form, savedRequest: snap.savedRequest, configureInFlight: false })})`;
    paintDebug();
  }

  function onFormEdited() {
    session.setFormRequest(readDomForm());
    syncFallbackEnabled();
    updateApplyEnabled();
  }

  function syncFormFromOutput(incomingOutput) {
    const out = incomingOutput || state.output;
    if (!out) {
      updateApplyEnabled();
      return;
    }

    lastDomModeBeforeSnapshot = dom['out-mode'] ? dom['out-mode'].value : null;
    const before = session.snapshot();
    const beforeDirty = before.formDirty;
    const beforeMode = lastDomModeBeforeSnapshot;
    const beforeInFlight = before.configureInFlight;
    const after = session.receiveOutput(out);

    // Bridge/refresh may still be pending; matching output clears inFlight so Apply is usable.
    if (beforeInFlight && !after.configureInFlight && after.lastReconcile) {
      if (!after.formDirty) {
        applyFormValues(logic.formValuesFromRequest(after.savedRequest));
      }
      toastConfigureResult(out);
      updateApplyEnabled();
      paintDebug({
        snapshotDomModeBefore: lastDomModeBeforeSnapshot,
        snapshotDomModeAfter: dom['out-mode'] && dom['out-mode'].value,
        reconciledFromOutput: 'yes',
      });
      return;
    }

    if (after.lastSnapshotClobber) {
      // Host requested config changed while the form was clean — rewrite editable controls.
      applyFormValues(logic.formValuesFromRequest(after.savedRequest));
      if (beforeMode !== (dom['out-mode'] && dom['out-mode'].value)) {
        snapshotClobberCount += 1;
      }
    } else if (beforeDirty && beforeMode !== (dom['out-mode'] && dom['out-mode'].value)) {
      // DOM mode changed without going through onFormEdited — count as unexpected clobber.
      snapshotClobberCount += 1;
    }

    updateApplyEnabled();
    paintDebug({
      snapshotDomModeBefore: lastDomModeBeforeSnapshot,
      snapshotDomModeAfter: dom['out-mode'] && dom['out-mode'].value,
    });
  }

  function showFormError(message) {
    dom['out-form-error'].hidden = !message;
    setText(dom['out-form-error'], message || '');
  }

  async function applyConfiguration(event) {
    event.preventDefault();
    applyClickCount += 1;

    const form = readDomForm();
    session.setFormRequest(form);

    const begin = session.beginApply();
    paintDebug({ applyAttempt: begin.ok ? 'begin' : `blocked:${begin.reason}` });
    if (!begin.ok) {
      toast(begin.reason === 'configureInFlight'
        ? 'An output configuration change is already in progress.'
        : 'No output configuration changes to apply.', 'info');
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
        session.completeApplyFailure();
        updateApplyEnabled();
        return;
      }
    }

    const request = {
      mode: form.mode,
      exclusiveFallback: form.exclusiveFallback,
      requestedLatencyMs: latency,
    };

    updateApplyEnabled();
    try {
      const result = await run('output.configure', [request], { label: 'Output configuration', toast: false });

      // Complete the session BEFORE refresh/snapshot. A hung refresh must not leave
      // configureInFlight stuck (which permanently disables Apply).
      if (result.ok) {
        if (result.value) TP.core.setOutput(result.value);
        const out = result.value || state.output;
        const alreadyReconciled = !session.snapshot().configureInFlight;
        session.completeApplySuccess(out, { preserveDirtyForm: true });
        if (!session.snapshot().formDirty) {
          applyFormValues(logic.formValuesFromRequest(session.getSavedRequest()));
        }
        if (!alreadyReconciled) {
          toastConfigureResult(out);
        }
        paintDebug({ lastSent: JSON.stringify(request), applySettled: alreadyReconciled ? 'afterReconcile' : 'fromBridge' });
      } else {
        const message = result.message || 'Output configuration failed.';
        showFormError(message);
        toast(`Output configuration: ${message}`, 'danger');
        session.completeApplyFailure();
      }

      try {
        await refresh({ quiet: true });
        await TP.core.refreshSnapshot();
      } catch (refreshErr) {
        console.warn('Post-configure refresh failed:', refreshErr);
        paintDebug({ refreshError: String(refreshErr && refreshErr.message ? refreshErr.message : refreshErr) });
      }
    } catch (err) {
      if (session.snapshot().configureInFlight) {
        session.completeApplyFailure();
      }
      showFormError(String(err && err.message ? err.message : err));
      throw err;
    } finally {
      updateApplyEnabled();
    }
  }

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

  function init() {
    resolveDom();
    TP.ui.checklist(dom['output-checklist'], 'output', CHECKLIST);

    dom['btn-refresh-devices'].addEventListener('click', () => refresh());
    dom['output-form'].addEventListener('submit', applyConfiguration);
    // Capture phase on the button itself so we log clicks even if submit is suppressed.
    dom['btn-apply-output'].addEventListener('click', () => {
      applyClickCount += 1;
      paintDebug({ rawButtonClick: 'yes' });
    }, true);

    dom['out-latency-default'].addEventListener('change', onFormEdited);
    dom['out-mode'].addEventListener('input', onFormEdited);
    dom['out-mode'].addEventListener('change', onFormEdited);
    dom['out-fallback'].addEventListener('input', onFormEdited);
    dom['out-fallback'].addEventListener('change', onFormEdited);
    dom['out-latency'].addEventListener('input', () => {
      showFormError('');
      onFormEdited();
    });

    TP.bus.on('devices', () => {
      renderDevices();
    });
    TP.bus.on('output', (payload) => {
      const out = payload || state.output;
      // Host often settles to Available before devices.set returns; drop the local
      // spinner as soon as the preferred device matches the Use click target.
      if (switchingId !== undefined && out && !logic.isOutputSwitching(out)
        && outputMatchesSwitchTarget(out, switchingId)) {
        switchingId = undefined;
      }
      renderEffective();
      renderDevices();
      syncFormFromOutput(out);
    });

    updateApplyEnabled();
    paintDebug({ init: 'ok' });
    return refresh({ quiet: true });
  }

  TP.output = {
    init,
    refresh,
    deviceName,
    activeLabel,
    /** Runtime diagnostics for DevTools: copy(window.__TP_OUTPUT_DEBUG__) */
    getDebug: () => window.__TP_OUTPUT_DEBUG__,
  };
})();
