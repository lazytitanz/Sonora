/*
 * Pure helpers for the Audio Output configuration form.
 * Kept free of DOM / bridge dependencies so Node can regression-test dirty detection.
 */
(function (root) {
  'use strict';

  const DEFAULT_LATENCY_INPUT_MS = 100;

  function normalizeMode(value) {
    const v = String(value || 'shared').trim().toLowerCase();
    return v === 'exclusive' ? 'exclusive' : 'shared';
  }

  function normalizeFallback(value) {
    const v = String(value || 'none').trim().toLowerCase();
    return v === 'shared' ? 'shared' : 'none';
  }

  function normalizeLatencyMs(value) {
    if (value == null || value === '') return null;
    const n = typeof value === 'number' ? value : Number(value);
    if (!Number.isFinite(n)) return null;
    return Math.round(n);
  }

  /** Builds the comparable requested-config object from engine output state. */
  function requestedFromOutput(out) {
    if (!out) {
      return { mode: 'shared', exclusiveFallback: 'none', requestedLatencyMs: null };
    }
    return {
      mode: normalizeMode(out.mode),
      exclusiveFallback: normalizeFallback(out.exclusiveFallback),
      requestedLatencyMs: normalizeLatencyMs(out.requestedLatencyMs),
    };
  }

  /** Builds the comparable requested-config object from editable form values. */
  function requestedFromForm({ mode, exclusiveFallback, useDefaultLatency, latencyMs }) {
    return {
      mode: normalizeMode(mode),
      exclusiveFallback: normalizeFallback(exclusiveFallback),
      requestedLatencyMs: useDefaultLatency ? null : normalizeLatencyMs(latencyMs),
    };
  }

  function requestsEqual(a, b) {
    if (!a || !b) return false;
    return (
      a.mode === b.mode
      && a.exclusiveFallback === b.exclusiveFallback
      && a.requestedLatencyMs === b.requestedLatencyMs
    );
  }

  /**
   * Apply is enabled only when the form differs from the last saved requested config
   * and no configure request is in flight.
   */
  function shouldEnableApply({ formRequest, savedRequest, configureInFlight }) {
    if (configureInFlight) return false;
    return !requestsEqual(formRequest, savedRequest);
  }

  /**
   * Whether an incoming engine output payload should rewrite the editable form.
   * Effective-only changes (status, active device, latency readout) must not clobber edits.
   */
  function shouldSyncFormFromOutput({ formDirty, savedRequest, incomingOutput }) {
    if (formDirty) return false;
    const incoming = requestedFromOutput(incomingOutput);
    return !requestsEqual(savedRequest, incoming);
  }

  function formValuesFromRequest(request) {
    const req = request || requestedFromOutput(null);
    const useDefault = req.requestedLatencyMs == null;
    return {
      mode: req.mode,
      exclusiveFallback: req.exclusiveFallback,
      useDefaultLatency: useDefault,
      latencyMs: useDefault ? DEFAULT_LATENCY_INPUT_MS : req.requestedLatencyMs,
    };
  }

  const api = {
    DEFAULT_LATENCY_INPUT_MS,
    normalizeMode,
    normalizeFallback,
    normalizeLatencyMs,
    requestedFromOutput,
    requestedFromForm,
    requestsEqual,
    shouldEnableApply,
    shouldSyncFormFromOutput,
    formValuesFromRequest,
  };

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = api;
  }
  root.TP = root.TP || {};
  root.TP.outputFormLogic = api;
})(typeof globalThis !== 'undefined' ? globalThis : window);
