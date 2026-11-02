using Sonora.Audio.Network;
using Sonora.TestPlayer.Services;

namespace Sonora.TestPlayer.Models;

/// <summary>Streaming state and safety-mode flags reported to the UI.</summary>
public sealed record StreamingStatusDto
{
    /// <summary>URL-safety mode currently in effect for new loads.</summary>
    public UrlSafetyMode SafetyMode { get; init; }

    /// <summary>Whether the developer override that allows local/private hosts is active.</summary>
    public bool AllowLocalDeveloperMode { get; init; }

    /// <summary>
    /// Always <see langword="false"/>. The URL checks are best-effort pre-flight checks, not a complete SSRF defense.
    /// </summary>
    public bool SsrfProtectionComplete { get; init; }

    /// <summary>Plain-language statement of what the safety checks do not cover (show this in the UI).</summary>
    public string Limitation { get; init; } = StreamingLimitations.Text;

    /// <summary>Whether a stream load is currently in flight.</summary>
    public bool Loading { get; init; }

    /// <summary>Identifier of the most recently loaded stream, or <see langword="null"/>.</summary>
    public string? ActiveUrl { get; init; }

    /// <summary>Network transport status reported by the player.</summary>
    public NetworkTransportStatus NetworkStatus { get; init; }

    /// <summary>Bytes held in the network buffer.</summary>
    public int NetworkBytesBuffered { get; init; }

    /// <summary>Last load failure message, or <see langword="null"/>.</summary>
    public string? LastError { get; init; }
}

/// <summary>Result of a stream load request.</summary>
/// <param name="Accepted">Whether the URL passed pre-flight validation and a load was attempted.</param>
/// <param name="Reason">Validation explanation (why it was blocked, allowed, or allowed by override).</param>
/// <param name="Cancelled">Whether the load was cancelled before completing.</param>
/// <param name="Status">Streaming status after the call.</param>
public sealed record StreamingLoadResult(bool Accepted, string Reason, bool Cancelled, StreamingStatusDto Status);

/// <summary>Shared limitation text for streaming safety.</summary>
public static class StreamingLimitations
{
    /// <summary>Text describing the boundaries of the URL safety checks.</summary>
    public const string Text =
        "URL checks are best-effort only. Sonora's HTTP transport exposes no DNS-resolution or redirect hooks, so "
        + "host names that resolve to private addresses and HTTP redirects to local hosts are NOT detected. "
        + "Do not treat this as SSRF protection.";
}
