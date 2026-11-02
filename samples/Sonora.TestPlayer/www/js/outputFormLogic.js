/*
 * Pure helpers + FormSession for the Audio Output configuration form.
 * Free of DOM / bridge dependencies so Node can run integration regressions.
 */
(function (root) {
  'use strict';

  const DEFAULT_LATENCY_INPUT_MS = 100;
  const ASSET_BUILD_ID = 'output-form-2026-11-02c';

  function normalizeMode(value) {
    // Vetra may emit camelCase strings or numeric JsonStringEnum fallbacks.
    if (value === 1 || value === '1') return 'exclusive';
    if (value === 0 || value === '0') return 'shared';
    const v = String(value == null ? 'shared' : value).trim().toLowerCase();
    return v === 'exclusive' ? 'exclusive' : 'shared';
  }

  function normalizeFallback(value) {
    if (value === 1 || value === '1') return 'shared';
    if (value === 0 || value === '0') return 'none';
    const v = String(value == null ? 'none' : value).trim().toLowerCase();
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
    // Accept camelCase or accidental PascalCase property names from the host.
    const mode = out.mode != null ? out.mode : out.Mode;
    const exclusiveFallback = out.exclusiveFallback != null ? out.exclusiveFallback : out.ExclusiveFallback;
    const requestedLatencyMs = out.requestedLatencyMs != null ? out.requestedLatencyMs : out.RequestedLatencyMs;
    return {
      mode: normalizeMode(mode),
      exclusiveFallback: normalizeFallback(exclusiveFallback),
      requestedLatencyMs: normalizeLatencyMs(requestedLatencyMs),
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

  function shouldEnableApply({ formRequest, savedRequest, configureInFlight }) {
    if (configureInFlight) return false;
    return !requestsEqual(formRequest, savedRequest);
  }

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

  function applyDisabledReason({ formRequest, savedRequest, configureInFlight }) {
    if (configureInFlight) return 'configureInFlight';
    if (requestsEqual(formRequest, savedRequest)) return 'formMatchesSavedRequest';
    return null;
  }

  /**
   * Stateful session used by the UI and by Node integration tests.
   * Tracks saved requested config independently of effective mode.
   */
  function createFormSession(initialOutput) {
    let savedRequest = requestedFromOutput(initialOutput || null);
    let formRequest = { ...savedRequest };
    let formDirty = false;
    let configureInFlight = false;
    let pendingRequest = null;
    let lastClick = null;
    let lastSnapshotClobber = false;
    let lastReconcile = null;
    const sentRequests = [];

    function snapshot() {
      return {
        assetBuildId: ASSET_BUILD_ID,
        formDirty,
        configureInFlight,
        pendingRequest: pendingRequest ? { ...pendingRequest } : null,
        savedRequest: { ...savedRequest },
        formRequest: { ...formRequest },
        equal: requestsEqual(formRequest, savedRequest),
        applyEnabled: shouldEnableApply({ formRequest, savedRequest, configureInFlight }),
        disabledReason: applyDisabledReason({ formRequest, savedRequest, configureInFlight }),
        lastClick,
        lastSnapshotClobber,
        lastReconcile,
        sentRequests: sentRequests.slice(),
      };
    }

    function selectMode(mode) {
      return setFormRequest({ ...formRequest, mode });
    }

    function selectFallback(fallback) {
      return setFormRequest({ ...formRequest, exclusiveFallback: fallback });
    }

    /** Replace the editable requested config (from the DOM) and mark dirty. */
    function setFormRequest(next) {
      formRequest = requestedFromForm({
        mode: next.mode,
        exclusiveFallback: next.exclusiveFallback,
        useDefaultLatency: next.requestedLatencyMs == null,
        latencyMs: next.requestedLatencyMs,
      });
      formDirty = true;
      lastSnapshotClobber = false;
      return snapshot();
    }

    /**
     * Host output snapshot. While an Apply is in flight, a matching requested config
     * completes the apply even if the bridge promise is still pending (or refresh hangs).
     * Dirty user edits (e.g. switching back to Shared) are preserved.
     */
    function receiveOutput(output) {
      if (configureInFlight && pendingRequest) {
        const incoming = requestedFromOutput(output);
        if (requestsEqual(incoming, pendingRequest)) {
          return completeApplySuccess(output, { preserveDirtyForm: true, source: 'outputMatchedPending' });
        }
      }

      const sync = shouldSyncFormFromOutput({ formDirty, savedRequest, incomingOutput: output });
      if (sync) {
        savedRequest = requestedFromOutput(output);
        formRequest = { ...savedRequest };
        formDirty = false;
        lastSnapshotClobber = true;
      } else {
        lastSnapshotClobber = false;
      }
      return snapshot();
    }

    function beginApply() {
      lastClick = {
        at: Date.now(),
        formRequest: { ...formRequest },
        savedRequest: { ...savedRequest },
        applyEnabled: shouldEnableApply({ formRequest, savedRequest, configureInFlight }),
      };
      if (!shouldEnableApply({ formRequest, savedRequest, configureInFlight })) {
        return { ok: false, reason: applyDisabledReason({ formRequest, savedRequest, configureInFlight }), snapshot: snapshot() };
      }
      pendingRequest = { ...formRequest };
      configureInFlight = true;
      lastReconcile = null;
      return { ok: true, request: { ...pendingRequest }, snapshot: snapshot() };
    }

    function completeApplySuccess(output, options = {}) {
      const preserveDirtyForm = options.preserveDirtyForm === true && formDirty;
      if (pendingRequest) {
        sentRequests.push({ ...pendingRequest });
        pendingRequest = null;
      }
      savedRequest = requestedFromOutput(output);
      configureInFlight = false;
      if (!preserveDirtyForm || requestsEqual(formRequest, savedRequest)) {
        // No divergent edit (or edit matches the new saved request) — sync clean.
        formRequest = { ...savedRequest };
        formDirty = false;
      }
      lastSnapshotClobber = false;
      if (options.source) lastReconcile = options.source;
      return snapshot();
    }

    function completeApplyFailure() {
      // Keep the attempted form values for retry.
      pendingRequest = null;
      formDirty = true;
      configureInFlight = false;
      return snapshot();
    }

    return {
      snapshot,
      selectMode,
      selectFallback,
      setFormRequest,
      receiveOutput,
      beginApply,
      completeApplySuccess,
      completeApplyFailure,
      getSavedRequest: () => ({ ...savedRequest }),
      getFormRequest: () => ({ ...formRequest }),
      getPendingRequest: () => (pendingRequest ? { ...pendingRequest } : null),
    };
  }

  const api = {
    ASSET_BUILD_ID,
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
    applyDisabledReason,
    createFormSession,
  };

  if (typeof module !== 'undefined' && module.exports) {
    module.exports = api;
  }
  root.TP = root.TP || {};
  root.TP.outputFormLogic = api;
})(typeof globalThis !== 'undefined' ? globalThis : window);
