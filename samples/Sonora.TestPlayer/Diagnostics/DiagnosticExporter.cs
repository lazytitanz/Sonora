using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Diagnostics;

/// <summary>
/// Exports a <see cref="DiagnosticSnapshot"/> as JSON or a human-readable summary.
/// </summary>
/// <remarks>
/// Unless <c>includeSensitive</c> is set, every string value is redacted: full file paths are reduced to the
/// file name, the current user's profile directory is replaced with <c>%USERPROFILE%</c>, and URL
/// query strings, fragments, and credentials are removed. Redaction is best-effort pattern matching over
/// text, so review a report before sharing it publicly. All members are thread-safe.
/// </remarks>
public static partial class DiagnosticExporter
{
    private const string UserProfilePlaceholder = "%USERPROFILE%";
    private const string RedactedPathPlaceholder = "[path]";
    private const int SummaryEventLimit = 50;

    /// <summary>Serializer settings used for exports (camelCase, string enums, indented, nulls omitted).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // A "path character" that never runs into the start of another drive path (so two paths in one sentence
    // are redacted independently).
    private const string PathChar = """(?:(?![A-Za-z]:[\\/])[^\\/:*?"<>|\r\n])""";

    [GeneratedRegex(@"https?://[^\s""'<>)\]]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:[A-Za-z]:[\\/]|\\\\[^\\/\s]+[\\/]|%USERPROFILE%[\\/])(?:" + PathChar + @"+[\\/])*" + PathChar + "*",
        RegexOptions.CultureInvariant)]
    private static partial Regex PathRegex();

    /// <summary>Serializes the snapshot to JSON, redacting unless <paramref name="includeSensitive"/> is set.</summary>
    public static string ExportJson(DiagnosticSnapshot snapshot, bool includeSensitive)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var node = JsonSerializer.SerializeToNode(snapshot, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("Snapshot did not serialize to a JSON object.");

        node["redacted"] = !includeSensitive;
        if (!includeSensitive)
        {
            RedactInPlace(node);
        }

        return node.ToJsonString(JsonOptions);
    }

    /// <summary>Builds a plain-text summary, redacting unless <paramref name="includeSensitive"/> is set.</summary>
    public static string ExportTextSummary(DiagnosticSnapshot snapshot, bool includeSensitive)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = BuildSummary(snapshot, includeSensitive);
        return includeSensitive ? text : Redact(text);
    }

    /// <summary>Writes export text to a file (UTF-8, no BOM), creating or replacing it.</summary>
    public static async Task WriteToFileAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Redacts a single string: URL query/fragment/credentials removed, user profile directory replaced, and
    /// full file paths reduced to their file name.
    /// </summary>
    /// <param name="value">Text to redact.</param>
    /// <param name="userProfilePath">
    /// Profile directory to replace; defaults to the current user's profile. Exposed for deterministic tests.
    /// </param>
    public static string Redact(string value, string? userProfilePath = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return value;
        }

        var result = UrlRegex().Replace(value, m => RedactUrl(m.Value));

        var profile = userProfilePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var trimmed = profile.TrimEnd('\\', '/');
            if (trimmed.Length > 0)
            {
                result = result.Replace(trimmed, UserProfilePlaceholder, StringComparison.OrdinalIgnoreCase);
                result = result.Replace(
                    trimmed.Replace('\\', '/'),
                    UserProfilePlaceholder,
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return PathRegex().Replace(result, m => FileNameOnly(m.Value));
    }

    private static string RedactUrl(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        var trimmed = cut >= 0 ? url[..cut] : url;

        // Remove "user:password@" from the authority.
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var authorityStart = schemeEnd + 3;
            var authorityEnd = trimmed.IndexOf('/', authorityStart);
            if (authorityEnd < 0)
            {
                authorityEnd = trimmed.Length;
            }

            var at = trimmed.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
            if (at >= authorityStart)
            {
                trimmed = string.Concat(trimmed.AsSpan(0, authorityStart), trimmed.AsSpan(at + 1));
            }
        }

        return trimmed;
    }

    private static string FileNameOnly(string path)
    {
        var index = path.LastIndexOfAny(['\\', '/']);
        var name = index >= 0 ? path[(index + 1)..] : path;
        return name.Length == 0 ? RedactedPathPlaceholder : name;
    }

    private static void RedactInPlace(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        obj[key] = Redact(s);
                    }
                    else
                    {
                        RedactInPlace(child);
                    }
                }

                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (child is JsonValue value && value.TryGetValue<string>(out var s))
                    {
                        array[i] = Redact(s);
                    }
                    else
                    {
                        RedactInPlace(child);
                    }
                }

                break;
        }
    }

    private static string BuildSummary(DiagnosticSnapshot s, bool includeSensitive)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Sonora Test Player diagnostic summary");
        sb.AppendLine(ci, $"Captured (UTC): {s.CapturedAtUtc:O}");
        sb.AppendLine(ci, $"Sensitive data included: {(includeSensitive ? "yes" : "no (redacted)")}");
        sb.AppendLine();

        sb.AppendLine("Environment");
        sb.AppendLine(ci, $"  OS: {s.Environment.Os}");
        sb.AppendLine(ci, $"  Runtime: {s.Environment.Runtime} ({s.Environment.ProcessArchitecture})");
        sb.AppendLine(ci, $"  Sonora: {s.Environment.SonoraVersion}");
        sb.AppendLine(ci, $"  Test player: {s.Environment.TestPlayerVersion}");
        sb.AppendLine();

        if (s.Engine is { } e)
        {
            sb.AppendLine("Engine");
            sb.AppendLine(ci, $"  State: {e.State}; source: {e.SourcePath ?? "(none)"}");
            sb.AppendLine(ci, $"  Position: {e.PositionMs:F0} ms / {(e.HasKnownDuration ? e.DurationMs.ToString("F0", ci) : "unknown")} ms; volume {e.Volume:F2}");
            sb.AppendLine(ci, $"  Queue: {e.QueueLength} item(s), index {e.QueueIndex}; gapless {(e.GaplessEnabled ? "on" : "off")}; crossfade {(e.CrossfadeDurationMs <= 0 ? "off" : $"{e.CrossfadeDurationMs:0.#}ms {e.CrossfadeCurve}")}; next {e.NextTrackState} {e.NextTrackPath}");
            sb.AppendLine(ci, $"  Network: {e.NetworkStatus}, {e.NetworkBytesBuffered} bytes buffered");
            sb.AppendLine(ci, $"  Counters: transitions {e.TransitionsObserved}, completions {e.CompletionsObserved} (advances {e.CompletionAdvances}), errors {e.ErrorsObserved}, underrun events {e.UnderrunEventsObserved}, engine underruns {e.UnderrunCount}");
            if (e.Output is { } o)
            {
                sb.AppendLine(ci, $"  Output: requested {o.Mode} (fallback {o.ExclusiveFallback}, latency {o.RequestedLatencyMs?.ToString("F0", ci) ?? "default"} ms); effective {o.EffectiveMode?.ToString() ?? "none"}; fallback reason {o.FallbackReason}; device status {o.DeviceStatus}");
            }

            sb.AppendLine();
        }

        if (s.Dsp is { } d)
        {
            sb.AppendLine("DSP");
            sb.AppendLine(ci, $"  Rack bypass: {d.RackBypass}; preamp {(d.PreampEnabled ? "on" : "off")} {d.PreampGainDb:F1} dB");
            sb.AppendLine(ci, $"  EQ {(d.EqualizerEnabled ? "on" : "off")}: {string.Join(", ", d.Bands.Select(b => $"{b.FrequencyHz:F0}Hz={b.GainDb:F1}dB"))}");
            sb.AppendLine(ci, $"  Limiter {(d.LimiterEnabled ? "on" : "off")}: ceiling {d.LimiterCeilingDb:F1} dBFS, release {d.LimiterReleaseMs:F0} ms, look-ahead {d.LimiterLookaheadMs:F1} ms (construction-time)");
            sb.AppendLine();
        }

        if (s.Process is { } p)
        {
            sb.AppendLine("Process resources (whole process, not Sonora-attributable)");
            sb.AppendLine(ci, $"  Working set {p.WorkingSetBytes / 1048576.0:F1} MiB; private {p.PrivateBytes / 1048576.0:F1} MiB; managed {p.GcTotalMemoryBytes / 1048576.0:F1} MiB");
            sb.AppendLine(ci, $"  Handles {p.HandleCount}; threads {p.ThreadCount}; GC gen0/1/2 {p.Gen0Collections}/{p.Gen1Collections}/{p.Gen2Collections}; uptime {p.UptimeSeconds:F0} s");
            sb.AppendLine();
        }

        if (s.Scenarios is { } sc)
        {
            sb.AppendLine("Scenarios");
            foreach (var r in sc.Results)
            {
                sb.Append(ci, $"  [{r.Status}] {r.ScenarioId}");
                if (!string.IsNullOrWhiteSpace(r.Notes))
                {
                    sb.Append(ci, $" — {r.Notes}");
                }

                sb.AppendLine();
            }

            var soak = sc.Soak;
            sb.AppendLine(ci, $"  Soak: {(soak.Active ? "running" : soak.StartedAtUtc is null ? "not started" : "finished")}; elapsed {soak.ElapsedSeconds:F0} s; errors {soak.ErrorCount}; underruns {soak.UnderrunCount}");
            foreach (var note in soak.Notes)
            {
                sb.AppendLine(ci, $"    note: {note}");
            }

            sb.AppendLine();
        }

        var notable = s.Events.Where(ev => ev.Level >= DiagnosticLevel.Warning).TakeLast(SummaryEventLimit).ToList();
        sb.AppendLine(ci, $"Recent warnings/errors ({notable.Count} of {s.TotalEventsRecorded} total events recorded)");
        foreach (var ev in notable)
        {
            sb.AppendLine(ci, $"  #{ev.Seq} +{ev.ElapsedMs:F0}ms [{ev.Level}] {ev.Name}: {ev.Message}");
        }

        return sb.ToString();
    }
}
