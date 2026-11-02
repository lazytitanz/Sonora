/*
 * dsp.js — rack bypass, presets, preamp, 6-band equalizer and limiter controls.
 *
 * Every DSP command returns the full DSP state; the UI always renders the controller's answer rather than
 * assuming the requested value was applied. Slider drags are coalesced with latestWins() so a slow response can
 * never overwrite a newer value.
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { el, clear, setText, setRangeFill, latestWins } = TP.ui;
  const { run } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;

  const MAX_GAIN_DB = 24;
  const MIN_RELEASE_MS = 1;
  const MAX_RELEASE_MS = 5000;
  const RELEASE_SLIDER_MAX = 1000;
  const USER_INPUT_GRACE_MS = 600;

  const dom = {};
  const bandControls = []; // { band, wrap, slider, gain, enable, send }
  const touched = new WeakMap();

  const RELEASE_LOG_SPAN = Math.log(MAX_RELEASE_MS / MIN_RELEASE_MS);
  const releaseFromSlider = (t) => Math.max(MIN_RELEASE_MS, Math.round(MIN_RELEASE_MS * Math.exp((t / RELEASE_SLIDER_MAX) * RELEASE_LOG_SPAN)));
  const sliderFromRelease = (ms) => Math.round((Math.log(Math.max(MIN_RELEASE_MS, ms) / MIN_RELEASE_MS) / RELEASE_LOG_SPAN) * RELEASE_SLIDER_MAX);

  function resolveDom() {
    const ids = [
      'dsp-bypass', 'dsp-bypass-note', 'btn-dsp-reset', 'dsp-presets', 'dsp-active-preset', 'dsp-preamp-enabled',
      'dsp-preamp-gain', 'dsp-preamp-value', 'dsp-eq-enabled', 'dsp-bands', 'dsp-limiter-enabled', 'dsp-ceiling',
      'dsp-ceiling-value', 'dsp-release', 'dsp-release-value', 'dsp-lookahead', 'dsp-checklist',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  const touch = (node) => touched.set(node, performance.now());
  const recentlyTouched = (node) => performance.now() - (touched.get(node) || -Infinity) < USER_INPUT_GRACE_MS;

  function freqLabel(hz) {
    if (hz >= 1000) {
      const k = hz / 1000;
      return `${Number.isInteger(k) ? k : k.toFixed(1)} kHz`;
    }
    return `${Math.round(hz)} Hz`;
  }

  const gainLabel = (db) => `${fmt.db(db)} dB`;

  // ---------------------------------------------------------------------------------------------
  // Commands
  // ---------------------------------------------------------------------------------------------

  async function command(name, args, label) {
    const result = await run(name, args, { label });
    if (result.ok && result.value) {
      TP.core.setDsp(result.value);
    } else if (!result.ok) {
      await refresh({ quiet: true }); // resync the UI with what the controller really has
    }
    return result;
  }

  async function refresh({ quiet = false } = {}) {
    const result = await run('dsp.getState', [], { toast: !quiet, label: 'DSP state' });
    if (result.ok) TP.core.setDsp(result.value);
  }

  // ---------------------------------------------------------------------------------------------
  // Rendering
  // ---------------------------------------------------------------------------------------------

  function ensureBands(bands) {
    if (bandControls.length === bands.length && bands.length > 0) return;
    bandControls.length = 0;
    const host = clear(dom['dsp-bands']);
    for (const band of bands) {
      const index = band.index;
      const slider = el('input', {
        type: 'range',
        class: 'tp-range tp-range-v',
        min: String(-MAX_GAIN_DB),
        max: String(MAX_GAIN_DB),
        step: '0.5',
        value: '0',
        'aria-label': `Band ${index + 1} gain, ${freqLabel(band.frequencyHz)}`,
        title: 'Double-click to reset to 0 dB',
      });
      const gain = el('div', { class: 'eq-gain', text: '0.0 dB' });
      const enableId = `dsp-band-enable-${index}`;
      const enable = el('input', { class: 'form-check-input', type: 'checkbox', id: enableId, 'aria-label': `Band ${index + 1} enabled` });
      const wrap = el(
        'div',
        { class: 'eq-band' },
        gain,
        slider,
        el('div', { class: 'eq-freq', text: freqLabel(band.frequencyHz) }),
        el('div', { class: 'form-check form-switch m-0 d-flex justify-content-center' }, enable),
      );

      const send = latestWins(async (value) => {
        await command('dsp.setBandGain', [index, value], `Band ${index + 1} gain`);
      });
      slider.addEventListener('input', () => {
        touch(slider);
        const value = Number(slider.value);
        setText(gain, gainLabel(value));
        send(value);
      });
      slider.addEventListener('dblclick', () => {
        touch(slider);
        slider.value = '0';
        setText(gain, gainLabel(0));
        send(0);
      });
      enable.addEventListener('change', () => {
        touch(enable);
        command('dsp.setBandEnabled', [index, enable.checked], `Band ${index + 1}`);
      });

      bandControls.push({ band: index, wrap, slider, gain, enable });
      host.append(wrap);
    }
  }

  function render() {
    const s = state.dsp;
    if (!s) return;

    dom['dsp-bypass'].checked = Boolean(s.rackBypass);
    dom['dsp-bypass-note'].hidden = !s.rackBypass;

    // Presets
    const presets = Array.isArray(s.presetNames) ? s.presetNames : [];
    const host = dom['dsp-presets'];
    const existing = Array.from(host.children).map((c) => c.dataset.preset);
    if (existing.length !== presets.length || existing.some((n, i) => n !== presets[i])) {
      clear(host);
      for (const name of presets) {
        const chip = el('button', { type: 'button', class: 'btn btn-xs btn-chip', dataset: { preset: name }, text: name });
        chip.addEventListener('click', () => command('dsp.applyPreset', [name], `Preset ${name}`));
        host.append(chip);
      }
    }
    for (const chip of host.children) {
      const active = s.activePreset != null && chip.dataset.preset.toLowerCase() === String(s.activePreset).toLowerCase();
      chip.classList.toggle('active', active);
      chip.setAttribute('aria-pressed', String(active));
    }
    setText(dom['dsp-active-preset'], s.activePreset || 'Custom (edited)');

    // Preamp
    dom['dsp-preamp-enabled'].checked = Boolean(s.preampEnabled);
    if (!recentlyTouched(dom['dsp-preamp-gain'])) {
      dom['dsp-preamp-gain'].value = String(s.preampGainDb);
      setText(dom['dsp-preamp-value'], gainLabel(s.preampGainDb));
    }
    setRangeFill(dom['dsp-preamp-gain']);
    dom['dsp-preamp-gain'].closest('.panel').classList.toggle('eq-inactive', Boolean(s.rackBypass) || !s.preampEnabled);

    // Equalizer
    dom['dsp-eq-enabled'].checked = Boolean(s.equalizerEnabled);
    ensureBands(s.bands || []);
    for (const bandState of s.bands || []) {
      const control = bandControls[bandState.index];
      if (!control) continue;
      if (!recentlyTouched(control.slider)) {
        control.slider.value = String(bandState.gainDb);
        setText(control.gain, gainLabel(bandState.gainDb));
      }
      if (!recentlyTouched(control.enable)) control.enable.checked = Boolean(bandState.enabled);
      control.wrap.classList.toggle('is-off', !bandState.enabled);
    }
    dom['dsp-bands'].closest('.panel').classList.toggle('eq-inactive', Boolean(s.rackBypass) || !s.equalizerEnabled);

    // Limiter
    dom['dsp-limiter-enabled'].checked = Boolean(s.limiterEnabled);
    if (!recentlyTouched(dom['dsp-ceiling'])) {
      dom['dsp-ceiling'].value = String(s.limiterCeilingDb);
      setText(dom['dsp-ceiling-value'], `${fmt.db(s.limiterCeilingDb)} dBFS`);
    }
    setRangeFill(dom['dsp-ceiling']);
    if (!recentlyTouched(dom['dsp-release'])) {
      dom['dsp-release'].value = String(sliderFromRelease(s.limiterReleaseMs));
      setText(dom['dsp-release-value'], `${Math.round(s.limiterReleaseMs)} ms`);
    }
    setRangeFill(dom['dsp-release']);
    setText(dom['dsp-lookahead'], typeof s.limiterLookaheadMs === 'number' ? `${s.limiterLookaheadMs.toFixed(1)} ms` : 'Unavailable');
    dom['dsp-ceiling'].closest('.panel').classList.toggle('eq-inactive', Boolean(s.rackBypass) || !s.limiterEnabled);
  }

  // ---------------------------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------------------------

  const CHECKLIST = [
    'Apply Bass Boost, then Flat: an audible low-end change with no clicks while switching.',
    'Drag a single band while music plays: the change is smooth and the preset name becomes “Custom”.',
    'Disable the equalizer, then individual bands: each behaves as 0 dB.',
    'Raise the preamp to +12 dB on loud material with the limiter off: clipping is audible.',
    'Enable the limiter at 0 dBFS, then −6 dBFS: the output no longer clips and the ceiling change is audible.',
    'Toggle “Bypass entire rack” with an extreme EQ curve: the unprocessed sound returns without clicks.',
    'Reset: Flat preset and 0 dB preamp.',
  ];

  function wire() {
    dom['dsp-bypass'].addEventListener('change', () => command('dsp.setRackBypass', [dom['dsp-bypass'].checked], 'Rack bypass'));
    dom['btn-dsp-reset'].addEventListener('click', () => command('dsp.reset', [], 'Reset'));

    dom['dsp-preamp-enabled'].addEventListener('change', () => command('dsp.setPreampEnabled', [dom['dsp-preamp-enabled'].checked], 'Preamp'));
    dom['dsp-eq-enabled'].addEventListener('change', () => command('dsp.setEqEnabled', [dom['dsp-eq-enabled'].checked], 'Equalizer'));
    dom['dsp-limiter-enabled'].addEventListener('change', () => command('dsp.setLimiterEnabled', [dom['dsp-limiter-enabled'].checked], 'Limiter'));

    const sendPreamp = latestWins((value) => command('dsp.setPreampGain', [value], 'Preamp gain'));
    const preamp = dom['dsp-preamp-gain'];
    const applyPreamp = (value) => {
      touch(preamp);
      preamp.value = String(value);
      setText(dom['dsp-preamp-value'], gainLabel(value));
      setRangeFill(preamp);
      sendPreamp(value);
    };
    preamp.addEventListener('input', () => applyPreamp(Number(preamp.value)));
    preamp.addEventListener('dblclick', () => applyPreamp(0));

    const sendCeiling = latestWins((value) => command('dsp.setLimiterCeiling', [value], 'Limiter ceiling'));
    const ceiling = dom['dsp-ceiling'];
    ceiling.addEventListener('input', () => {
      touch(ceiling);
      const value = Number(ceiling.value);
      setText(dom['dsp-ceiling-value'], `${fmt.db(value)} dBFS`);
      setRangeFill(ceiling);
      sendCeiling(value);
    });
    ceiling.addEventListener('dblclick', () => {
      touch(ceiling);
      ceiling.value = '0';
      setText(dom['dsp-ceiling-value'], `${fmt.db(0)} dBFS`);
      setRangeFill(ceiling);
      sendCeiling(0);
    });

    const sendRelease = latestWins((value) => command('dsp.setLimiterRelease', [value], 'Limiter release'));
    const release = dom['dsp-release'];
    release.addEventListener('input', () => {
      touch(release);
      const ms = releaseFromSlider(Number(release.value));
      setText(dom['dsp-release-value'], `${ms} ms`);
      setRangeFill(release);
      sendRelease(ms);
    });
  }

  function init() {
    resolveDom();
    TP.ui.checklist(dom['dsp-checklist'], 'dsp', CHECKLIST);
    wire();
    TP.bus.on('dsp', render);
    return refresh({ quiet: true });
  }

  TP.dsp = { init, refresh };
})();
