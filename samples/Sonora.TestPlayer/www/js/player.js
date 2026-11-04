/*
 * player.js — playback bar, queue, current-source panel, gapless panel, seek tests and the manual checklist.
 *
 * Unit note: the host's playback.seek / playback.seekWithResult take MILLISECONDS. The UI works in seconds and
 * converts at the call site (seekSeconds below), so the rest of the file never mixes units.
 */
(function () {
  'use strict';

  const TP = window.TP;
  const { $, el, clear, setText, setPill, setRangeFill, latestWins, toast } = TP.ui;
  const { run } = TP.bridge;
  const fmt = TP.fmt;
  const state = TP.state;

  const SEEK_HISTORY_MAX = 6;
  const SEEK_HOLD_MS = 450;
  const DEFAULT_RESTORE_VOLUME = 0.8;

  const dom = {};
  let seekDragging = false;
  let seekHoldUntil = 0;
  /** True while the pointer is down on the volume slider. */
  let volumeDragging = false;
  /**
   * Authoritative level from the newest in-flight/completed setVolume that is not yet reflected in
   * state.snapshot.volume. Keeps stale engine.snapshot / position renders from snapping the thumb back.
   */
  let volumePending = null;
  let volumeEpoch = 0;
  let lastNonZeroVolume = DEFAULT_RESTORE_VOLUME;
  let artworkFor = null;
  let artworkResolved = false;
  let dragId = null;
  let gaplessBusy = false;
  /** True while the user is editing crossfade controls so snapshots do not clobber in-progress input. */
  let crossfadeBusy = false;
  const seekHistory = [];

  // ---------------------------------------------------------------------------------------------
  // Helpers
  // ---------------------------------------------------------------------------------------------

  function resolveDom() {
    const ids = [
      'pb-art-img', 'pb-art-ph', 'pb-title', 'pb-artist', 'pb-prev', 'pb-stop', 'pb-play', 'pb-play-icon', 'pb-next',
      'pb-elapsed', 'pb-seek', 'pb-duration', 'pb-mute', 'pb-mute-icon', 'pb-volume', 'pb-volume-value', 'pb-state',
      'pb-device', 'pb-device-text',
      'queue-count', 'queue-list', 'queue-empty', 'btn-add-folder', 'btn-add-files', 'btn-load-file', 'btn-clear-queue', 'path-input',
      'btn-add-path', 'btn-load-path', 'dialogs-note',
      'np-art-img', 'np-art-ph', 'np-title', 'np-artist', 'np-album', 'np-format', 'np-source', 'np-queue-pos', 'np-generation',
      'gapless-switch', 'gapless-next-state', 'gapless-next-path', 'gapless-transitions', 'gapless-completions',
      'gapless-advances', 'btn-preload-next', 'btn-clear-next',
      'crossfade-duration', 'crossfade-curve',
      'seek-input', 'btn-seek', 'btn-seek-result', 'seek-result', 'seek-history', 'player-checklist',
    ];
    for (const id of ids) dom[id] = document.getElementById(id);
  }

  function isActivelyPlaying(snap) {
    return Boolean(snap) && (snap.state === 'playing' || snap.state === 'buffering');
  }

  function currentItem() {
    const snap = state.snapshot;
    if (!snap) return null;
    const id = snap.currentItemId;
    if (state.currentItem && (!id || state.currentItem.id === id)) return state.currentItem;
    return state.queue.find((i) => i.id === id) || null;
  }

  function sampleRateLabel(item) {
    const parts = [];
    if (item.sampleRate) parts.push(`${Number((item.sampleRate / 1000).toFixed(1))} kHz`);
    if (item.channelCount) parts.push(`${item.channelCount} ch`);
    return parts.join(' · ');
  }

  async function refreshSnapshot() {
    await TP.core.refreshSnapshot();
  }

  // ---------------------------------------------------------------------------------------------
  // Artwork (only validated image data: / _artwork URLs ever reach <img src>)
  // ---------------------------------------------------------------------------------------------

  function isSafeArtworkUrl(url) {
    if (typeof url !== 'string' || !url) return false;
    if (/^data:image\/[a-z0-9.+-]+;base64,/i.test(url) && !/^data:image\/svg/i.test(url)) return true;
    // Host-written files under the Vetra web root (see ArtworkFileCache).
    return /^(?:\.\/|\/)?_artwork\/[A-Za-z0-9._-]+\.(?:jpe?g|png|gif|webp|bmp)$/i.test(url);
  }

  function setArtwork(url) {
    const ok = isSafeArtworkUrl(url);
    for (const [img, ph] of [[dom['pb-art-img'], dom['pb-art-ph']], [dom['np-art-img'], dom['np-art-ph']]]) {
      if (ok) {
        img.onerror = () => {
          img.removeAttribute('src');
          img.hidden = true;
          ph.hidden = false;
          // Allow syncArtwork to retry after a failed image fetch.
          artworkFor = null;
          artworkResolved = false;
        };
        img.src = url;
        img.hidden = false;
        ph.hidden = true;
      } else {
        img.onerror = null;
        img.removeAttribute('src');
        img.hidden = true;
        ph.hidden = false;
      }
    }
  }

  async function syncArtwork(preferredId) {
    const snap = state.snapshot;
    const item = currentItem();
    // Prefer an explicit trackChanged id: snapshot.currentItemId can still be the previous track
    // when gapless TrackTransitioned fires (artwork was missing after Duration+10 s advances).
    const id = preferredId || (item && item.id) || (snap && snap.currentItemId) || null;
    if (id === artworkFor && artworkResolved) return;
    if (id === artworkFor && !artworkResolved) return; // request already in flight
    artworkFor = id;
    artworkResolved = false;
    setArtwork(null);
    if (!id) {
      artworkResolved = true;
      return;
    }
    const artItem = (item && item.id === id) ? item : (state.queue.find((i) => i.id === id) || item);
    if (artItem && artItem.isStream) {
      artworkResolved = true;
      return;
    }
    if (artItem && isSafeArtworkUrl(artItem.artworkDataUrl)) {
      setArtwork(artItem.artworkDataUrl);
      artworkResolved = true;
      return;
    }
    const result = await run('playback.getArtwork', [id], { toast: false });
    if (artworkFor !== id) return; // a newer track superseded this request
    if (result.ok && isSafeArtworkUrl(result.value)) {
      setArtwork(result.value);
      artworkResolved = true;
      return;
    }
    setArtwork(null);
    if (result.ok) {
      // Host answered definitively (null / rejected URL) — do not hammer.
      artworkResolved = true;
      return;
    }
    // Invoke failed: clear claim so a later snapshot can retry.
    artworkFor = null;
    artworkResolved = false;
  }

  // ---------------------------------------------------------------------------------------------
  // Playback bar
  // ---------------------------------------------------------------------------------------------

  function renderTransport() {
    const snap = state.snapshot;
    const ready = Boolean(snap) && !snap.disposed && snap.engineCreated;
    const playing = isActivelyPlaying(snap);

    // Play / pause button
    const playIcon = dom['pb-play-icon'];
    playIcon.className = `bi ${playing ? 'bi-pause-fill' : 'bi-play-fill'}`;
    dom['pb-play'].title = playing ? 'Pause' : 'Play';
    dom['pb-play'].setAttribute('aria-label', playing ? 'Pause' : 'Play');
    dom['pb-play'].disabled = !ready || (!snap.hasSource && state.queue.length === 0);
    dom['pb-stop'].disabled = !ready || !snap.hasSource;
    const idx = ready ? snap.queueIndex : -1;
    const len = ready ? snap.queueLength : 0;
    dom['pb-prev'].disabled = !ready || idx <= 0;
    dom['pb-next'].disabled = !ready || len === 0 || idx + 1 >= len;

    // State chip
    const chip = dom['pb-state'];
    const stateName = ready ? snap.state : 'stopped';
    chip.dataset.state = stateName;
    let label = fmt.humanize(stateName);
    if (ready && snap.networkStatus && snap.networkStatus !== 'idle') label += ` · net ${fmt.humanize(snap.networkStatus).toLowerCase()}`;
    setText(chip, label);

    // Times and seek bar
    const known = ready && snap.hasKnownDuration && snap.durationMs > 0;
    const position = ready && snap.hasSource ? snap.positionMs : 0;
    const seek = dom['pb-seek'];
    if (!seekDragging) {
      setText(dom['pb-elapsed'], fmt.time(position));
    }
    setText(dom['pb-duration'], ready && snap.hasSource ? fmt.duration(snap.durationMs, snap.hasKnownDuration) : fmt.UNKNOWN_TIME);
    seek.disabled = !known;
    if (known) {
      const max = Math.floor(snap.durationMs);
      if (Number(seek.max) !== max) seek.max = String(max);
      if (!seekDragging && performance.now() > seekHoldUntil) {
        seek.value = String(Math.min(max, Math.max(0, Math.round(position))));
      }
    } else {
      seek.max = '0';
      seek.value = '0';
    }
    seek.setAttribute('aria-valuetext', known ? `${fmt.time(Number(seek.value))} of ${fmt.time(snap.durationMs)}` : 'Position unavailable');
    setRangeFill(seek);

    // Volume: never overwrite an active drag or an unsettled setVolume with a stale snapshot.
    if (ready && !volumeDragging && volumePending == null && typeof snap.volume === 'number') {
      const pct = Math.round(clampVolume(snap.volume) * 100);
      if (Number(dom['pb-volume'].value) !== pct) {
        dom['pb-volume'].value = String(pct);
      }
      if (snap.volume > 0) lastNonZeroVolume = clampVolume(snap.volume);
    }
    renderVolumeLabels();

    // Device
    const deviceText = TP.output && TP.output.activeLabel ? TP.output.activeLabel() : '—';
    setText(dom['pb-device-text'], deviceText);
    dom['pb-device'].title = `Output device: ${deviceText}`;
  }

  function clampVolume(level) {
    if (typeof level !== 'number' || !Number.isFinite(level)) return 0;
    return Math.min(1, Math.max(0, level));
  }

  function renderVolumeLabels() {
    const value = Number(dom['pb-volume'].value);
    setText(dom['pb-volume-value'], `${value}%`);
    setRangeFill(dom['pb-volume']);
    const icon = value === 0 ? 'bi-volume-mute-fill' : value < 40 ? 'bi-volume-down-fill' : 'bi-volume-up-fill';
    dom['pb-mute-icon'].className = `bi ${icon}`;
  }

  /**
   * Sends volume to the host. Uses latestWins so rapid drags coalesce. On success, merges the
   * returned engine volume into the shared snapshot so later renders stay aligned without a timer.
   */
  function createVolumeSender() {
    return latestWins(async (level) => {
      const epoch = ++volumeEpoch;
      const requested = clampVolume(level);
      volumePending = requested;
      const result = await run('playback.setVolume', [requested], { label: 'Volume', toast: false });
      if (epoch !== volumeEpoch) {
        return; // a newer command owns volumePending
      }

      if (result.ok) {
        const applied = clampVolume(typeof result.value === 'number' ? result.value : requested);
        volumePending = null;
        TP.core.mergeIntoSnapshot({ volume: applied });
        if (applied > 0) lastNonZeroVolume = applied;
        if (!volumeDragging) {
          const pct = Math.round(applied * 100);
          if (Number(dom['pb-volume'].value) !== pct) {
            dom['pb-volume'].value = String(pct);
          }
          renderVolumeLabels();
        }
        return;
      }

      volumePending = null;
      TP.ui.toast(result.message || 'Volume change failed', 'danger');
      // Resync from the engine so a failed command cannot leave the thumb lying.
      await TP.core.refreshSnapshot();
    });
  }

  function renderNowPlaying() {
    const snap = state.snapshot;
    const item = currentItem();
    const hasSource = Boolean(snap && snap.hasSource);
    const fallbackName = snap && snap.sourcePath ? fmt.name(snap.sourcePath) : null;

    const title = item ? item.title : fallbackName || (hasSource ? 'Loaded source' : 'Nothing loaded');
    const artistLine = item ? [item.artist, item.album].filter(Boolean).join(' — ') : '';
    setText(dom['pb-title'], title);
    setText(dom['pb-artist'], artistLine || '\u00a0');
    dom['pb-title'].title = title;

    setText(dom['np-title'], item ? item.title : fallbackName || '—');
    setText(dom['np-artist'], (item && item.artist) || '—');
    setText(dom['np-album'], (item && item.album) || '—');
    const formatParts = item ? [item.format, sampleRateLabel(item)].filter(Boolean) : [];
    setText(dom['np-format'], formatParts.length ? formatParts.join(' · ') : '—');
    setText(dom['np-source'], snap && snap.sourcePath ? fmt.name(snap.sourcePath) : '—');
    dom['np-source'].title = snap && snap.sourcePath ? (/^https?:/i.test(snap.sourcePath) ? fmt.name(snap.sourcePath) : snap.sourcePath) : '';
    if (snap && snap.queueIndex >= 0) setText(dom['np-queue-pos'], `${snap.queueIndex + 1} of ${snap.queueLength}`);
    else setText(dom['np-queue-pos'], hasSource ? 'Not in queue (stream or removed)' : '—');
    setText(dom['np-generation'], snap && snap.engineCreated ? String(snap.generation) : '—');
  }

  function renderGapless() {
    const snap = state.snapshot;
    const ready = Boolean(snap) && snap.engineCreated;
    if (!gaplessBusy) dom['gapless-switch'].checked = ready && Boolean(snap.gaplessEnabled);
    dom['gapless-switch'].disabled = !ready;
    const tones = { none: 'muted', loading: 'info', ready: 'ok', failed: 'bad' };
    const nextState = ready ? snap.nextTrackState : null;
    setPill(dom['gapless-next-state'], nextState ? fmt.humanize(nextState) : '—', tones[nextState] || 'muted');
    setText(dom['gapless-next-path'], ready && snap.nextTrackPath ? fmt.name(snap.nextTrackPath) : '—');
    dom['gapless-next-path'].title = ready && snap.nextTrackPath ? fmt.name(snap.nextTrackPath) : '';
    setText(dom['gapless-transitions'], ready ? String(snap.transitionsObserved) : '—');
    setText(dom['gapless-completions'], ready ? String(snap.completionsObserved) : '—');
    setText(dom['gapless-advances'], ready ? String(snap.completionAdvances) : '—');

    if (!crossfadeBusy) {
      const durationSec = ready && typeof snap.crossfadeDurationMs === 'number'
        ? snap.crossfadeDurationMs / 1000
        : 0;
      const durationStr = Number.isFinite(durationSec) ? String(Math.round(durationSec * 10) / 10) : '0';
      if (dom['crossfade-duration'].value !== durationStr) {
        dom['crossfade-duration'].value = durationStr;
      }
      const curve = ready && snap.crossfadeCurve === 'linear' ? 'linear' : 'equalPower';
      if (dom['crossfade-curve'].value !== curve) {
        dom['crossfade-curve'].value = curve;
      }
    }
    dom['crossfade-duration'].disabled = !ready;
    dom['crossfade-curve'].disabled = !ready;
  }

  function renderAll() {
    renderTransport();
    renderNowPlaying();
    renderGapless();
    updateQueueHighlight();
    syncArtwork();
  }

  // ---------------------------------------------------------------------------------------------
  // Transport commands
  // ---------------------------------------------------------------------------------------------

  async function onPlayPause() {
    const playing = isActivelyPlaying(state.snapshot);
    await run(playing ? 'playback.pause' : 'playback.play', [], { label: playing ? 'Pause' : 'Play' });
    await refreshSnapshot();
  }

  async function onStop() {
    await run('playback.stop', [], { label: 'Stop' });
    await refreshSnapshot();
  }

  async function onNextPrevious(command, emptyMessage) {
    const result = await run(command, [], { label: command === 'playback.next' ? 'Next' : 'Previous' });
    if (result.ok && result.value === false) toast(emptyMessage, 'info');
    await refreshSnapshot();
  }

  let seekCorrelationSeq = 0;

  async function seekSeconds(seconds) {
    const correlationId = ++seekCorrelationSeq;
    return run('playback.seek', [seconds * 1000, correlationId], { label: 'Seek' });
  }

  function wirePlaybar() {
    dom['pb-play'].addEventListener('click', onPlayPause);
    dom['pb-stop'].addEventListener('click', onStop);
    dom['pb-next'].addEventListener('click', () => onNextPrevious('playback.next', 'There is no next track in the queue.'));
    dom['pb-prev'].addEventListener('click', () => onNextPrevious('playback.previous', 'There is no previous track in the queue.'));

    const seek = dom['pb-seek'];
    seek.addEventListener('pointerdown', () => {
      seekDragging = true;
    });
    const endDrag = () => {
      seekDragging = false;
    };
    window.addEventListener('pointerup', endDrag);
    window.addEventListener('pointercancel', endDrag);
    seek.addEventListener('input', () => {
      setText(dom['pb-elapsed'], fmt.time(Number(seek.value)));
      setRangeFill(seek);
    });
    seek.addEventListener('change', async () => {
      seekHoldUntil = performance.now() + SEEK_HOLD_MS;
      await seekSeconds(Number(seek.value) / 1000);
      seekDragging = false;
      await refreshSnapshot();
    });

    const sendVolume = createVolumeSender();
    const volume = dom['pb-volume'];
    volume.addEventListener('pointerdown', () => {
      volumeDragging = true;
    });
    const endVolumeDrag = () => {
      if (!volumeDragging) return;
      volumeDragging = false;
      // If a command is still in flight, volumePending keeps snapshots from reverting the thumb.
      // If not, paint from the merged snapshot (or pending) immediately.
      if (volumePending == null && state.snapshot && typeof state.snapshot.volume === 'number') {
        const pct = Math.round(clampVolume(state.snapshot.volume) * 100);
        volume.value = String(pct);
      } else if (volumePending != null) {
        volume.value = String(Math.round(volumePending * 100));
      }
      renderVolumeLabels();
    };
    window.addEventListener('pointerup', endVolumeDrag);
    window.addEventListener('pointercancel', endVolumeDrag);
    volume.addEventListener('input', () => {
      const level = clampVolume(Number(volume.value) / 100);
      if (level > 0) lastNonZeroVolume = level;
      // Optimistically claim pending so a position tick cannot race the first await.
      volumePending = level;
      renderVolumeLabels();
      sendVolume(level);
    });
    dom['pb-mute'].addEventListener('click', () => {
      const current = clampVolume(Number(volume.value) / 100);
      const next = current > 0 ? 0 : lastNonZeroVolume || DEFAULT_RESTORE_VOLUME;
      if (current > 0) lastNonZeroVolume = current;
      volume.value = String(Math.round(next * 100));
      volumePending = next;
      renderVolumeLabels();
      sendVolume(next);
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Queue
  // ---------------------------------------------------------------------------------------------

  function renderQueue() {
    const list = clear(dom['queue-list']);
    const items = state.queue;
    setText(dom['queue-count'], String(items.length));
    dom['queue-empty'].hidden = items.length > 0;
    list.hidden = items.length === 0;

    items.forEach((item, index) => {
      const row = el('li', { class: 'queue-item', draggable: 'true', dataset: { id: item.id } });

      const sub = [item.artist, item.album].filter(Boolean).join(' — ');
      const meta = [];
      if (item.format) meta.push(el('span', { class: 'tp-pill', 'data-tone': 'muted', text: String(item.format).toUpperCase() }));
      const sr = sampleRateLabel(item);
      if (sr) meta.push(el('span', { text: sr }));
      meta.push(el('span', { text: fmt.time(item.durationMs == null ? NaN : item.durationMs), title: item.durationMs == null ? 'Duration unknown' : 'Container-reported duration' }));

      const button = (icon, title, handler, extra = {}) => {
        const b = el('button', { type: 'button', class: 'icon-btn', title, 'aria-label': title, ...extra }, el('i', { class: `bi ${icon}`, 'aria-hidden': 'true' }));
        b.addEventListener('click', (e) => {
          e.stopPropagation();
          handler();
        });
        return b;
      };

      row.append(
        el('div', { class: 'qi-index', text: String(index + 1) }),
        el('div', { class: 'qi-main' }, el('div', { class: 'qi-title', text: item.title, title: item.path }), el('div', { class: 'qi-sub', text: sub || fmt.name(item.path) })),
        el('div', { class: 'qi-meta' }, meta),
        el(
          'div',
          { class: 'qi-actions' },
          button('bi-play-fill', `Play ${item.title}`, () => playItem(item.id)),
          button('bi-chevron-up', 'Move up', () => moveItem(item.id, index - 1), index === 0 ? { disabled: true } : {}),
          button('bi-chevron-down', 'Move down', () => moveItem(item.id, index + 1), index === items.length - 1 ? { disabled: true } : {}),
          button('bi-x-lg', 'Remove from queue', () => removeItem(item.id)),
        ),
      );
      row.addEventListener('dblclick', () => playItem(item.id));
      wireDrag(row, item.id);
      list.append(row);
    });
    updateQueueHighlight();
  }

  function updateQueueHighlight() {
    const snap = state.snapshot;
    const currentId = snap ? snap.currentItemId : null;
    const playing = isActivelyPlaying(snap);
    const paused = snap && snap.state === 'paused';
    dom['queue-list'].querySelectorAll('.queue-item').forEach((row, index) => {
      const isCurrent = Boolean(currentId) && row.dataset.id === currentId;
      row.classList.toggle('is-current', isCurrent);
      const cell = row.firstElementChild;
      if (isCurrent) {
        const iconClass = playing ? 'bi-volume-up-fill' : paused ? 'bi-pause-fill' : 'bi-record-fill';
        if (!cell.firstElementChild || !cell.firstElementChild.classList.contains(iconClass)) {
          clear(cell).append(el('i', { class: `bi ${iconClass}`, 'aria-hidden': 'true' }));
        }
      } else if (cell.firstElementChild) {
        setText(clear(cell), String(index + 1));
      }
    });
  }

  async function playItem(id) {
    await run('queue.play', [id], { label: 'Play' });
    await refreshSnapshot();
  }

  async function moveItem(id, newIndex) {
    if (newIndex < 0 || newIndex >= state.queue.length) return;
    await run('queue.move', [id, newIndex], { label: 'Move' });
  }

  async function removeItem(id) {
    const result = await run('queue.remove', [id], { label: 'Remove' });
    if (result.ok && result.value === false) toast('That item is no longer in the queue.', 'info');
  }

  function wireDrag(row, id) {
    row.addEventListener('dragstart', (e) => {
      dragId = id;
      row.classList.add('dragging');
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', id);
    });
    row.addEventListener('dragend', () => {
      dragId = null;
      row.classList.remove('dragging');
      clearDropMarks();
    });
    row.addEventListener('dragover', (e) => {
      if (!dragId || dragId === id) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      const rect = row.getBoundingClientRect();
      const after = e.clientY > rect.top + rect.height / 2;
      clearDropMarks();
      row.classList.add(after ? 'drop-after' : 'drop-before');
    });
    row.addEventListener('dragleave', () => row.classList.remove('drop-before', 'drop-after'));
    row.addEventListener('drop', async (e) => {
      if (!dragId || dragId === id) return;
      e.preventDefault();
      const after = row.classList.contains('drop-after');
      clearDropMarks();
      const from = state.queue.findIndex((i) => i.id === dragId);
      const to = state.queue.findIndex((i) => i.id === id);
      if (from < 0 || to < 0) return;
      const insertAt = after ? to + 1 : to; // position in the list before removal
      const newIndex = insertAt > from ? insertAt - 1 : insertAt; // queue.move inserts after removing the item
      const moving = dragId;
      dragId = null;
      if (newIndex !== from) await moveItem(moving, newIndex);
    });
  }

  function clearDropMarks() {
    dom['queue-list'].querySelectorAll('.drop-before, .drop-after').forEach((n) => n.classList.remove('drop-before', 'drop-after'));
  }

  // ---------------------------------------------------------------------------------------------
  // Adding / loading files
  // ---------------------------------------------------------------------------------------------

  async function addPaths(paths) {
    const cleaned = paths.map((p) => String(p).trim().replace(/^"(.*)"$/, '$1')).filter(Boolean);
    if (cleaned.length === 0) return;
    const result = await run('queue.addFiles', [cleaned], { label: 'Add files' });
    if (!result.ok) return;
    const added = Array.isArray(result.value) ? result.value.length : 0;
    if (added === 0) toast('No audio files were added (not found, empty folder, or unreadable). See the Diagnostics log.', 'warning');
    else toast(`Added ${added} file${added === 1 ? '' : 's'} to the queue.`, 'success');
  }

  async function loadPath(path) {
    const cleaned = String(path).trim().replace(/^"(.*)"$/, '$1');
    if (!cleaned) return;
    const result = await run('playback.load', [cleaned], { label: 'Load' });
    if (result.ok) toast('Loaded. Press Play to start.', 'success');
    await refreshSnapshot();
  }

  function musicInitialDirectory() {
    const folder = state.musicFolder;
    return typeof folder === 'string' && folder.length > 0 ? folder : undefined;
  }

  async function addFolderViaDialog() {
    const result = await run('queue.pickAndAddFolder', [], { label: 'Add folder' });
    if (!result.ok) return;
    if (result.value == null) return; // cancelled
    const added = Array.isArray(result.value) ? result.value.length : 0;
    if (added === 0) toast('No audio files found in that folder (including subfolders).', 'warning');
    else toast(`Added ${added} file${added === 1 ? '' : 's'} from the folder.`, 'success');
  }

  async function addViaDialog() {
    if (!state.dialogsEnabled) {
      toast('File dialogs are not enabled. Paste a path instead.', 'warning');
      dom['path-input'].focus();
      return;
    }
    const options = { title: 'Add audio files', filters: TP.bridge.AUDIO_FILTERS };
    const initial = musicInitialDirectory();
    if (initial) options.initialDirectory = initial;
    const picked = await TP.bridge.dialogs.openFiles(options);
    if (picked.ok && Array.isArray(picked.value) && picked.value.length > 0) await addPaths(picked.value);
  }

  async function loadViaDialog() {
    if (!state.dialogsEnabled) {
      toast('File dialogs are not enabled. Paste a path instead.', 'warning');
      dom['path-input'].focus();
      return;
    }
    const options = { title: 'Load audio file', filters: TP.bridge.AUDIO_FILTERS };
    const initial = musicInitialDirectory();
    if (initial) options.initialDirectory = initial;
    const picked = await TP.bridge.dialogs.openFile(options);
    if (picked.ok && picked.value) await loadPath(picked.value);
  }

  function wireQueueControls() {
    dom['btn-add-folder'].addEventListener('click', addFolderViaDialog);
    dom['btn-add-files'].addEventListener('click', addViaDialog);
    dom['btn-load-file'].addEventListener('click', loadViaDialog);
    dom['btn-clear-queue'].addEventListener('click', async () => {
      await run('queue.clear', [], { label: 'Clear queue' });
    });
    dom['btn-add-path'].addEventListener('click', async () => {
      const value = dom['path-input'].value;
      if (!value.trim()) return toast('Enter a file or folder path first.', 'warning');
      await addPaths([value]);
      dom['path-input'].value = '';
    });
    dom['btn-load-path'].addEventListener('click', async () => {
      const value = dom['path-input'].value;
      if (!value.trim()) return toast('Enter a file path first.', 'warning');
      await loadPath(value);
      dom['path-input'].value = '';
    });
    dom['path-input'].addEventListener('keydown', (e) => {
      if (e.key === 'Enter') dom['btn-add-path'].click();
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Gapless
  // ---------------------------------------------------------------------------------------------

  function wireGapless() {
    dom['gapless-switch'].addEventListener('change', async () => {
      gaplessBusy = true;
      const enabled = dom['gapless-switch'].checked;
      const result = await run('playback.setGapless', [enabled], { label: 'Gapless' });
      gaplessBusy = false;
      if (!result.ok) dom['gapless-switch'].checked = !enabled;
      await refreshSnapshot();
    });
    dom['btn-preload-next'].addEventListener('click', async () => {
      // The host preloads the queue item following the current one; it takes no arguments.
      const button = dom['btn-preload-next'];
      button.disabled = true;
      const result = await run('playback.preloadNext', [], { label: 'Preload' });
      button.disabled = false;
      await refreshSnapshot();
      if (result.ok) {
        const s = state.snapshot;
        toast(s && s.nextTrackState === 'ready' ? 'Next track preloaded.' : 'Preload finished; no next track is ready (gapless off, nothing loaded, or no next item).', s && s.nextTrackState === 'ready' ? 'success' : 'info');
      }
    });
    dom['btn-clear-next'].addEventListener('click', async () => {
      await run('playback.clearNext', [], { label: 'Clear next' });
      await refreshSnapshot();
    });

    const applyCrossfadeDuration = latestWins(async () => {
      crossfadeBusy = true;
      const seconds = Number(dom['crossfade-duration'].value);
      const ms = Number.isFinite(seconds) ? Math.max(0, Math.min(30, seconds)) * 1000 : 0;
      const result = await run('playback.setCrossfadeDurationMs', [ms], { label: 'Crossfade duration', toast: false });
      if (result.ok && typeof result.value === 'number') {
        TP.core.mergeIntoSnapshot({ crossfadeDurationMs: result.value });
        const appliedSec = Math.round((result.value / 1000) * 10) / 10;
        dom['crossfade-duration'].value = String(appliedSec);
      }
      crossfadeBusy = false;
      if (!result.ok) await refreshSnapshot();
    });

    dom['crossfade-duration'].addEventListener('change', () => { applyCrossfadeDuration(); });
    dom['crossfade-duration'].addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        applyCrossfadeDuration();
      }
    });

    dom['crossfade-curve'].addEventListener('change', async () => {
      crossfadeBusy = true;
      const curve = dom['crossfade-curve'].value === 'linear' ? 'linear' : 'equalPower';
      const result = await run('playback.setCrossfadeCurve', [curve], { label: 'Crossfade curve' });
      if (result.ok && typeof result.value === 'string') {
        TP.core.mergeIntoSnapshot({ crossfadeCurve: result.value });
        dom['crossfade-curve'].value = result.value === 'linear' ? 'linear' : 'equalPower';
      }
      crossfadeBusy = false;
      if (!result.ok) await refreshSnapshot();
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Seek tests
  // ---------------------------------------------------------------------------------------------

  function seconds3(ms) {
    return `${(ms / 1000).toFixed(3)} s`;
  }

  function signedMs(ms) {
    return `${ms > 0 ? '+' : ms < 0 ? '−' : ''}${Math.abs(ms).toFixed(1)} ms`;
  }

  function pushHistory(text) {
    seekHistory.unshift(`${fmt.localTime(new Date().toISOString())}  ${text}`);
    seekHistory.length = Math.min(seekHistory.length, SEEK_HISTORY_MAX);
    const list = clear(dom['seek-history']);
    for (const line of seekHistory) list.append(el('li', { text: line, title: line }));
  }

  function showSeekResult(requestedSeconds, dto) {
    const accuracy = dto.accuracy;
    const tone = accuracy === 'exact' ? 'ok' : accuracy === 'approximate' ? 'warn' : 'muted';
    const hasActual = typeof dto.actualMs === 'number';
    const box = clear(dom['seek-result']);
    box.append(
      el(
        'dl',
        { class: 'kv kv-tight' },
        el('dt', { text: 'Typed' }), el('dd', { text: `${requestedSeconds} s` }),
        el('dt', { text: 'Requested (clamped)' }), el('dd', { text: seconds3(dto.requestedMs) }),
        el('dt', { text: 'Actual landing' }), el('dd', { text: hasActual ? seconds3(dto.actualMs) : 'Unknown (no decoder evidence)' }),
        el('dt', { text: 'Delta' }), el('dd', { text: typeof dto.deltaMs === 'number' ? signedMs(dto.deltaMs) : 'Unavailable' }),
        el('dt', { text: 'Accuracy' }), el('dd', null, TP.ui.pill(fmt.humanize(accuracy), tone)),
      ),
    );
    pushHistory(`req ${seconds3(dto.requestedMs)} → ${hasActual ? seconds3(dto.actualMs) : 'unknown'} (${accuracy})`);
  }

  function showSeekText(message, tone, historyText) {
    const box = clear(dom['seek-result']);
    box.append(el('div', { class: tone === 'bad' ? 'text-danger' : 'small text-body-secondary', text: message }));
    pushHistory(historyText || message);
  }

  // Host coalesces rapid seeks (latest-wins). Keep UI responsive: never block the next click behind
  // an in-flight seek, and never treat a real decoder "unknown" accuracy as a coalesced skip.
  let seekUiEpoch = 0;

  async function doSeek(withResult) {
    const seconds = Number.parseFloat(dom['seek-input'].value);
    if (!Number.isFinite(seconds)) {
      toast('Enter a number of seconds to seek to.', 'warning');
      return;
    }
    const epoch = ++seekUiEpoch;
    const correlationId = ++seekCorrelationSeq;
    const ms = seconds * 1000;
    if (withResult) {
      const result = await run('playback.seekWithResult', [ms, correlationId], { toast: false });
      if (epoch !== seekUiEpoch) return; // a newer click owns the result panel
      if (result.ok) {
        if (result.value && result.value.coalesced) {
          // Host dropped this ticket behind a newer target — leave the panel for the latest call.
          return;
        }
        showSeekResult(seconds, result.value);
      } else {
        showSeekText(`Rejected: ${result.message}`, 'bad', `${seconds} s rejected`);
        toast(`Seek rejected: ${result.message}`, 'warning');
      }
    } else {
      const result = await run('playback.seek', [ms, correlationId], { toast: false });
      if (epoch !== seekUiEpoch) return;
      if (result.ok) showSeekText(`Seek to ${seconds} s issued. Plain seek reports no landing position; use “Seek with result” for that.`, 'muted', `${seconds} s issued (no result)`);
      else {
        showSeekText(`Rejected: ${result.message}`, 'bad', `${seconds} s rejected`);
        toast(`Seek rejected: ${result.message}`, 'warning');
      }
    }
    if (epoch === seekUiEpoch) await refreshSnapshot();
  }

  function presetSeconds(kind) {
    const snap = state.snapshot;
    const duration = snap && snap.hasKnownDuration && snap.durationMs > 0 ? snap.durationMs / 1000 : null;
    switch (kind) {
      case 'start': return 0;
      case 'negative': return -1;
      case 'middle': return duration == null ? null : duration / 2;
      case 'nearEnd': return duration == null ? null : Math.max(0, duration - 2);
      case 'pastEnd': return duration == null ? null : duration + 10;
      default: return null;
    }
  }

  function wireSeekTests() {
    dom['btn-seek'].addEventListener('click', () => doSeek(false));
    dom['btn-seek-result'].addEventListener('click', () => doSeek(true));
    dom['seek-input'].addEventListener('keydown', (e) => {
      if (e.key === 'Enter') doSeek(true);
    });
    document.querySelectorAll('[data-seek-preset]').forEach((button) => {
      button.addEventListener('click', () => {
        const seconds = presetSeconds(button.dataset.seekPreset);
        if (seconds == null) {
          toast('Duration is unknown, so duration-relative presets are unavailable.', 'warning');
          return;
        }
        dom['seek-input'].value = String(Number(seconds.toFixed(3)));
        doSeek(true);
      });
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Checklist
  // ---------------------------------------------------------------------------------------------

  const CHECKLIST = [
    'Play, pause (position holds), resume, stop (position resets to 0:00).',
    'Press Play / Pause / Stop / Next rapidly 20+ times: one audible session, no hang.',
    'Let the last track finish: one completion, state returns to Stopped.',
    'Press Stop near the end of a track: the queue does not advance.',
    'Gapless on: two tracks that join seamlessly play with no gap or click, and the queue index moves once.',
    'Gapless off: the next track starts after completion (a gap is expected), advancing once.',
    'Seek to start, middle and near end with “Seek with result”; compare requested vs. actual and the accuracy label.',
    'Seek to −1 s: rejected. Seek beyond the duration: clamps or rejects without a crash.',
    'Volume 0% is silent, 100% is full level, and dragging produces no clicks.',
    'Queue a corrupt or non-audio file: a clear error appears and the player stays usable.',
    'Remove and reorder upcoming tracks while playing: the following track respects the new order.',
  ];

  // ---------------------------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------------------------

  function init() {
    resolveDom();
    wirePlaybar();
    wireQueueControls();
    wireGapless();
    wireSeekTests();
    TP.ui.checklist(dom['player-checklist'], 'player', CHECKLIST);
    dom['dialogs-note'].hidden = state.dialogsEnabled;

    TP.bus.on('snapshot', renderAll);
    TP.bus.on('position', renderTransport);
    TP.bus.on('network', renderTransport);
    TP.bus.on('trackChanged', (payload) => {
      renderNowPlaying();
      renderTransport();
      updateQueueHighlight();
      // Force a fresh artwork resolve on every track change (gapless Duration+10 advances).
      artworkFor = null;
      artworkResolved = false;
      // Prefer artwork already attached to the track payload (thumbnail data URL from the host).
      if (payload && isSafeArtworkUrl(payload.artworkDataUrl)) {
        artworkFor = payload.id || artworkFor;
        artworkResolved = true;
        setArtwork(payload.artworkDataUrl);
        return;
      }
      // Pass payload.id so we do not keep the previous track's artwork when snapshot is stale.
      syncArtwork(payload && payload.id ? payload.id : null);
    });
    TP.bus.on('queue', () => {
      renderQueue();
      renderTransport();
    });
    TP.bus.on('devices', renderTransport);
    TP.bus.on('output', renderTransport);
    TP.bus.on('capabilities', () => {
      dom['dialogs-note'].hidden = state.dialogsEnabled;
    });
    TP.bus.on('completed', (payload) => {
      if (payload && payload.queueEnded) toast('Queue finished.', 'info');
    });

    renderQueue();
    renderAll();
  }

  TP.player = { init, renderAll, renderQueue };
})();
