using Sonora.Audio.Sources;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;
using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Controllers;

/// <summary>
/// Loads HTTP/HTTPS audio streams into the player after best-effort URL validation.
/// </summary>
/// <remarks>
/// <para><b>SSRF limitation.</b> Validation uses <see cref="UrlSafetyValidator"/>, which only inspects the URL text.
/// Sonora's HTTP transport (<see cref="HttpAudioSource"/>) does <b>not</b> expose DNS-resolution or redirect hooks, so
/// this controller cannot verify where a request ultimately connects. Host names that resolve to private addresses
/// (DNS rebinding) and redirects to local hosts are not detected. The result and status expose
/// <see cref="StreamingStatusDto.SsrfProtectionComplete"/> = <see langword="false"/> and the mode flags so the UI can
/// say so. In <see cref="UrlSafetyMode.Restricted"/> mode (default) local/private literal hosts are denied;
/// <see cref="UrlSafetyMode.DeveloperAllowLocal"/> is an explicit override for testing against local servers.</para>
/// <para><b>Ownership/threading.</b> The player belongs to <see cref="PlaybackController"/>. Methods are thread-safe;
/// a new load cancels any earlier in-flight stream load.</para>
/// <para>Stream loads are detached from the queue: completion of a stream does not advance the queue.</para>
/// </remarks>
public sealed class StreamingController
{
    private readonly PlaybackController _playback;
    private readonly DiagnosticEventStore _store;
    private readonly object _sync = new();
    private CancellationTokenSource? _loadCts;
    private bool _allowLocal;
    private bool _loading;
    private string? _activeUrl;
    private string? _lastError;

    /// <summary>Creates the controller.</summary>
    public StreamingController(PlaybackController playback, DiagnosticEventStore store)
    {
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Validates and loads a stream. Validation failures are reported in the result rather than thrown.
    /// </summary>
    /// <param name="url">Absolute http/https URL.</param>
    /// <param name="allowLocalDeveloperMode">Developer override allowing local/private hosts.</param>
    /// <param name="play">Whether to start playback after loading.</param>
    /// <exception cref="Sonora.Audio.AudioException">Connecting or decoding failed (propagated from the engine).</exception>
    public async Task<StreamingLoadResult> LoadUrlAsync(
        string url,
        bool allowLocalDeveloperMode,
        bool play,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _allowLocal = allowLocalDeveloperMode;
        }

        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            Log(DiagnosticLevel.Warning, "stream.rejected", "Rejected: not a valid absolute URL.");
            return new StreamingLoadResult(false, "Value is not a valid absolute URL.", false, GetStatus());
        }

        var (ok, reason) = UrlSafetyValidator.Validate(uri, allowLocalDeveloperMode);
        var safeLabel = DiagnosticExporter.Redact(uri.AbsoluteUri);
        if (!ok)
        {
            Log(DiagnosticLevel.Warning, "stream.blocked", $"Blocked: {reason}", ("url", safeLabel));
            return new StreamingLoadResult(false, reason, false, GetStatus());
        }

        if (allowLocalDeveloperMode)
        {
            Log(DiagnosticLevel.Warning, "stream.developerMode", $"Developer override in use. {reason}", ("url", safeLabel));
        }

        CancellationTokenSource cts;
        lock (_sync)
        {
            _loadCts?.Cancel();
            _loadCts = cts = new CancellationTokenSource();
            _loading = true;
            _lastError = null;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
            var source = HttpAudioSource.Create(uri);
            var item = new QueueItem(
                Id: Guid.NewGuid().ToString("N"),
                Path: source.Identifier,
                Title: BuildTitle(uri),
                Album: uri.Host,
                Format: "STREAM");

            Log(DiagnosticLevel.Info, "stream.loading", "Loading stream.", ("url", safeLabel));
            await _playback.LoadDetachedSourceAsync(source, item, play, linked.Token).ConfigureAwait(false);

            lock (_sync)
            {
                _activeUrl = source.Identifier;
            }

            return new StreamingLoadResult(true, reason, false, GetStatus());
        }
        catch (OperationCanceledException)
        {
            Log(DiagnosticLevel.Info, "stream.cancelled", "Stream load cancelled.", ("url", safeLabel));
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new StreamingLoadResult(true, reason, true, GetStatus());
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _lastError = PlaybackController.Describe(ex);
            }

            Log(DiagnosticLevel.Error, "stream.failed", "Stream load failed: " + PlaybackController.Describe(ex), ("url", safeLabel));
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_loadCts, cts))
                {
                    _loadCts = null;
                    _loading = false;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Cancels an in-flight stream load, if any.</summary>
    /// <returns><see langword="true"/> when a load was in flight.</returns>
    public bool CancelLoad()
    {
        lock (_sync)
        {
            if (_loadCts is null)
            {
                return false;
            }

            _loadCts.Cancel();
            return true;
        }
    }

    /// <summary>Returns safety-mode flags and current network status.</summary>
    public StreamingStatusDto GetStatus()
    {
        var player = _playback.Player;
        bool allowLocal;
        bool loading;
        string? activeUrl;
        string? lastError;
        lock (_sync)
        {
            allowLocal = _allowLocal;
            loading = _loading;
            activeUrl = _activeUrl;
            lastError = _lastError;
        }

        return new StreamingStatusDto
        {
            SafetyMode = UrlSafetyValidator.ModeFor(allowLocal),
            AllowLocalDeveloperMode = allowLocal,
            SsrfProtectionComplete = false,
            Loading = loading,
            ActiveUrl = activeUrl,
            NetworkStatus = player?.NetworkTransportStatus ?? default,
            NetworkBytesBuffered = player?.NetworkBytesBuffered ?? 0,
            LastError = lastError,
        };
    }

    private static string BuildTitle(Uri uri)
    {
        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return string.IsNullOrWhiteSpace(segment) ? uri.Host : Uri.UnescapeDataString(segment);
    }

    private void Log(DiagnosticLevel level, string name, string message, params (string Key, string Value)[] data) =>
        _store.Add("network", level, name, message, data);
}
