/**
 * Node regression tests for Audio Output form dirty / Apply enablement logic.
 * Run: node --test tests/Sonora.TestPlayer.Tests/outputFormLogic.test.js
 */
const { describe, it } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const logic = require(path.resolve(
  __dirname,
  '../../samples/Sonora.TestPlayer/www/js/outputFormLogic.js'));

describe('outputFormLogic', () => {
  it('treats exclusive→shared form edits as dirty against saved exclusive request', () => {
    const saved = logic.requestedFromOutput({
      mode: 'exclusive',
      exclusiveFallback: 'none',
      requestedLatencyMs: null,
    });
    const form = logic.requestedFromForm({
      mode: 'shared',
      exclusiveFallback: 'none',
      useDefaultLatency: true,
      latencyMs: 100,
    });
    assert.equal(logic.requestsEqual(saved, form), false);
    assert.equal(
      logic.shouldEnableApply({ formRequest: form, savedRequest: saved, configureInFlight: false }),
      true);
  });

  it('disables Apply while configure is in flight even when dirty', () => {
    const saved = logic.requestedFromOutput({ mode: 'shared' });
    const form = logic.requestedFromForm({
      mode: 'exclusive',
      exclusiveFallback: 'none',
      useDefaultLatency: true,
    });
    assert.equal(
      logic.shouldEnableApply({ formRequest: form, savedRequest: saved, configureInFlight: true }),
      false);
  });

  it('disables Apply when form matches saved requested config', () => {
    const saved = logic.requestedFromOutput({
      mode: 'exclusive',
      exclusiveFallback: 'shared',
      requestedLatencyMs: 50,
    });
    const form = logic.requestedFromForm({
      mode: 'Exclusive', // casing from host must not matter
      exclusiveFallback: 'Shared',
      useDefaultLatency: false,
      latencyMs: 50,
    });
    assert.equal(logic.requestsEqual(saved, form), true);
    assert.equal(
      logic.shouldEnableApply({ formRequest: form, savedRequest: saved, configureInFlight: false }),
      false);
  });

  it('does not sync form from effective-only output updates when clean', () => {
    const saved = logic.requestedFromOutput({
      mode: 'exclusive',
      exclusiveFallback: 'none',
      requestedLatencyMs: null,
    });
    const incoming = {
      mode: 'exclusive',
      exclusiveFallback: 'none',
      requestedLatencyMs: null,
      effectiveMode: 'exclusive',
      deviceStatus: 'available',
      activeDeviceId: 'dev-1',
      effectiveLatencyMs: 12.3,
    };
    assert.equal(
      logic.shouldSyncFormFromOutput({ formDirty: false, savedRequest: saved, incomingOutput: incoming }),
      false);
  });

  it('never syncs form while dirty, even if requested config changed on the host', () => {
    const saved = logic.requestedFromOutput({ mode: 'exclusive' });
    const incoming = { mode: 'shared', exclusiveFallback: 'none', requestedLatencyMs: null };
    assert.equal(
      logic.shouldSyncFormFromOutput({ formDirty: true, savedRequest: saved, incomingOutput: incoming }),
      false);
  });

  it('syncs form when clean and host requested mode actually changed', () => {
    const saved = logic.requestedFromOutput({ mode: 'exclusive' });
    const incoming = { mode: 'shared', exclusiveFallback: 'none', requestedLatencyMs: null };
    assert.equal(
      logic.shouldSyncFormFromOutput({ formDirty: false, savedRequest: saved, incomingOutput: incoming }),
      true);
  });
});
