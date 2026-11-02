/**
 * Integration-level regression for Exclusive ↔ Shared Apply enablement.
 * Simulates the UI session sequence without WebView2.
 *
 * Run: node --test tests/Sonora.TestPlayer.Tests/outputFormSession.integration.test.js
 */
const { describe, it } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');

const logic = require(path.resolve(
  __dirname,
  '../../samples/Sonora.TestPlayer/www/js/outputFormLogic.js'));

function outputState(partial) {
  return {
    mode: 'shared',
    exclusiveFallback: 'none',
    requestedLatencyMs: null,
    effectiveMode: 'shared',
    deviceStatus: 'available',
    ...partial,
  };
}

describe('output form session integration', () => {
  it('Shared → Exclusive apply → effective-only snapshots → Shared enables Apply and sends Shared', () => {
    const session = logic.createFormSession(outputState({ mode: 'shared', effectiveMode: 'shared' }));

    // 1. Start with Shared saved.
    assert.equal(session.snapshot().savedRequest.mode, 'shared');
    assert.equal(session.snapshot().applyEnabled, false);

    // 2. Select Exclusive.
    session.selectMode('exclusive');
    assert.equal(session.snapshot().formDirty, true);
    assert.equal(session.snapshot().applyEnabled, true);

    // 3. Apply successfully.
    const beginExclusive = session.beginApply();
    assert.equal(beginExclusive.ok, true);
    assert.equal(beginExclusive.request.mode, 'exclusive');
    session.completeApplySuccess(outputState({
      mode: 'exclusive',
      exclusiveFallback: 'none',
      effectiveMode: 'exclusive',
      activeDeviceId: 'dev-1',
      effectiveLatencyMs: 12,
    }));
    assert.equal(session.snapshot().savedRequest.mode, 'exclusive');
    assert.equal(session.snapshot().applyEnabled, false);
    assert.equal(session.snapshot().configureInFlight, false);

    // 4. Several effective-only updates must not clobber or disable a later Shared edit.
    for (let i = 0; i < 5; i += 1) {
      const snap = session.receiveOutput(outputState({
        mode: 'exclusive',
        exclusiveFallback: 'none',
        effectiveMode: 'exclusive',
        deviceStatus: i % 2 === 0 ? 'available' : 'switching',
        activeDeviceId: 'dev-1',
        effectiveLatencyMs: 10 + i,
      }));
      assert.equal(snap.formDirty, false);
      assert.equal(snap.savedRequest.mode, 'exclusive');
      assert.equal(snap.applyEnabled, false);
      assert.equal(snap.lastSnapshotClobber, false);
    }

    // 5. Select Shared.
    session.selectMode('shared');
    const afterSelect = session.snapshot();
    assert.equal(afterSelect.formRequest.mode, 'shared');
    assert.equal(afterSelect.savedRequest.mode, 'exclusive');
    assert.equal(afterSelect.formDirty, true);

    // 6. Apply becomes enabled.
    assert.equal(afterSelect.applyEnabled, true);
    assert.equal(afterSelect.disabledReason, null);

    // Effective-only noise while dirty must not reset the Shared selection.
    const whileDirty = session.receiveOutput(outputState({
      mode: 'exclusive',
      effectiveMode: 'exclusive',
      effectiveLatencyMs: 99,
    }));
    assert.equal(whileDirty.formRequest.mode, 'shared');
    assert.equal(whileDirty.formDirty, true);
    assert.equal(whileDirty.applyEnabled, true);
    assert.equal(whileDirty.lastSnapshotClobber, false);

    // 7–8. Click Apply; Shared is sent; UI reflects saved Shared.
    const beginShared = session.beginApply();
    assert.equal(beginShared.ok, true);
    assert.equal(beginShared.request.mode, 'shared');
    const afterShared = session.completeApplySuccess(outputState({
      mode: 'shared',
      exclusiveFallback: 'none',
      effectiveMode: 'shared',
    }));
    assert.equal(afterShared.savedRequest.mode, 'shared');
    assert.equal(afterShared.formRequest.mode, 'shared');
    assert.equal(afterShared.applyEnabled, false);
    assert.deepEqual(afterShared.sentRequests.map((r) => r.mode), ['exclusive', 'shared']);
  });

  it('in-flight Exclusive is reconciled from output events; selecting Shared then enables Apply', () => {
    const session = logic.createFormSession(outputState({ mode: 'shared', effectiveMode: 'shared' }));

    // Start Shared → select Exclusive → begin Apply (bridge promise still pending).
    session.selectMode('exclusive');
    const begin = session.beginApply();
    assert.equal(begin.ok, true);
    assert.equal(session.snapshot().configureInFlight, true);
    assert.equal(session.snapshot().applyEnabled, false);
    assert.equal(session.snapshot().disabledReason, 'configureInFlight');

    // Engine events report Exclusive before the bridge invoke settles.
    const reconciled = session.receiveOutput(outputState({
      mode: 'exclusive',
      exclusiveFallback: 'none',
      effectiveMode: 'exclusive',
      activeDeviceId: 'dev-1',
      effectiveLatencyMs: 50.1,
      hasSource: true,
    }));
    assert.equal(reconciled.configureInFlight, false);
    assert.equal(reconciled.savedRequest.mode, 'exclusive');
    assert.equal(reconciled.lastReconcile, 'outputMatchedPending');
    assert.equal(reconciled.applyEnabled, false);
    assert.deepEqual(reconciled.sentRequests.map((r) => r.mode), ['exclusive']);

    // More effective-only snapshots while clean.
    for (let i = 0; i < 3; i += 1) {
      const snap = session.receiveOutput(outputState({
        mode: 'exclusive',
        exclusiveFallback: 'none',
        effectiveMode: 'exclusive',
        effectiveLatencyMs: 50 + i,
      }));
      assert.equal(snap.configureInFlight, false);
      assert.equal(snap.savedRequest.mode, 'exclusive');
      assert.equal(snap.formRequest.mode, 'exclusive');
      assert.equal(snap.applyEnabled, false);
    }

    // Select Shared — Apply must enable (this is the stuck-button regression).
    session.selectMode('shared');
    const afterSharedSelect = session.snapshot();
    assert.equal(afterSharedSelect.formRequest.mode, 'shared');
    assert.equal(afterSharedSelect.savedRequest.mode, 'exclusive');
    assert.equal(afterSharedSelect.configureInFlight, false);
    assert.equal(afterSharedSelect.applyEnabled, true);
    assert.equal(afterSharedSelect.disabledReason, null);

    const beginShared = session.beginApply();
    assert.equal(beginShared.ok, true);
    assert.equal(beginShared.request.mode, 'shared');
    const afterShared = session.completeApplySuccess(outputState({
      mode: 'shared',
      exclusiveFallback: 'none',
      effectiveMode: 'shared',
    }));
    assert.equal(afterShared.savedRequest.mode, 'shared');
    assert.equal(afterShared.applyEnabled, false);
    assert.deepEqual(afterShared.sentRequests.map((r) => r.mode), ['exclusive', 'shared']);
  });

  it('selecting Shared while Exclusive apply is still in flight preserves Shared after reconcile', () => {
    const session = logic.createFormSession(outputState({ mode: 'shared' }));
    session.selectMode('exclusive');
    assert.equal(session.beginApply().ok, true);

    // User flips back to Shared before the Exclusive invoke returns.
    session.selectMode('shared');
    assert.equal(session.snapshot().formRequest.mode, 'shared');
    assert.equal(session.snapshot().configureInFlight, true);
    assert.equal(session.snapshot().applyEnabled, false);
    assert.equal(session.snapshot().disabledReason, 'configureInFlight');

    // Exclusive lands via output event — inFlight clears, Shared edit is kept, Apply enables.
    const after = session.receiveOutput(outputState({
      mode: 'exclusive',
      exclusiveFallback: 'none',
      effectiveMode: 'exclusive',
    }));
    assert.equal(after.configureInFlight, false);
    assert.equal(after.savedRequest.mode, 'exclusive');
    assert.equal(after.formRequest.mode, 'shared');
    assert.equal(after.formDirty, true);
    assert.equal(after.applyEnabled, true);
    assert.equal(after.disabledReason, null);

    const beginShared = session.beginApply();
    assert.equal(beginShared.ok, true);
    assert.equal(beginShared.request.mode, 'shared');
  });

  it('failed apply keeps form dirty so Apply stays usable for retry', () => {
    const session = logic.createFormSession(outputState({ mode: 'shared' }));
    session.selectMode('exclusive');
    assert.equal(session.beginApply().ok, true);
    const afterFail = session.completeApplyFailure();
    assert.equal(afterFail.formDirty, true);
    assert.equal(afterFail.configureInFlight, false);
    assert.equal(afterFail.applyEnabled, true);
  });

  it('exposes a non-empty asset build id for runtime verification', () => {
    assert.match(logic.ASSET_BUILD_ID, /^output-form-/);
    assert.equal(logic.ASSET_BUILD_ID, 'output-form-2026-11-02c');
  });
});
