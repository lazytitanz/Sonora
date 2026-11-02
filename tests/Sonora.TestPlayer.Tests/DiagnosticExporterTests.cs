using System.Text.Json.Nodes;
using Sonora.TestPlayer.Diagnostics;
using Sonora.TestPlayer.Models;

namespace Sonora.TestPlayer.Tests;

public sealed class DiagnosticExporterTests
{
    private const string FullPath = @"C:\Users\bob\Music\Secret Album\song.flac";
    private const string SignedUrl = "https://cdn.example.com/audio/a.mp3?token=abc123&sig=zzz#frag";

    [Fact]
    public void Redact_reduces_drive_paths_to_file_name()
    {
        Assert.Equal("song.flac", DiagnosticExporter.Redact(FullPath, @"C:\Users\someoneElse"));
        Assert.Equal("a.flac", DiagnosticExporter.Redact("C:/Music/a.flac"));
        Assert.Equal("a.flac", DiagnosticExporter.Redact(@"\\server\share\dir\a.flac"));
    }

    [Fact]
    public void Redact_keeps_surrounding_text_and_spaces_in_file_names()
    {
        var result = DiagnosticExporter.Redact(@"Could not open C:\Users\bob\a b.flac: access denied", @"C:\Users\someoneElse");

        Assert.Equal("Could not open a b.flac: access denied", result);
    }

    [Fact]
    public void Redact_handles_multiple_paths_in_one_string()
    {
        var result = DiagnosticExporter.Redact(@"Opened C:\Music\a.flac then C:\Music\b.flac", @"C:\Users\someoneElse");

        Assert.Equal("Opened a.flac then b.flac", result);
    }

    [Fact]
    public void Redact_replaces_user_profile_directory()
    {
        var result = DiagnosticExporter.Redact(@"Profile is C:\Users\bob and more", @"C:\Users\bob");

        Assert.Equal("Profile is %USERPROFILE% and more", result);
    }

    [Fact]
    public void Redact_strips_url_query_fragment_and_credentials()
    {
        Assert.Equal(
            "https://cdn.example.com/audio/a.mp3",
            DiagnosticExporter.Redact(SignedUrl));
        Assert.Equal(
            "Loading https://host.example/path/a.mp3 now",
            DiagnosticExporter.Redact("Loading https://user:pw@host.example/path/a.mp3?sig=1 now"));
    }

    [Fact]
    public void Redact_does_not_treat_url_scheme_as_drive_path()
    {
        Assert.Equal("https://example.com/a/b.mp3", DiagnosticExporter.Redact("https://example.com/a/b.mp3"));
    }

    [Fact]
    public void Redact_leaves_plain_text_untouched()
    {
        Assert.Equal("Underrun at 12:30 (3 episodes)", DiagnosticExporter.Redact("Underrun at 12:30 (3 episodes)"));
        Assert.Equal(string.Empty, DiagnosticExporter.Redact(string.Empty));
    }

    [Fact]
    public void ExportJson_redacts_paths_urls_and_data_by_default()
    {
        var json = DiagnosticExporter.ExportJson(CreateSnapshot(), includeSensitive: false);
        var root = JsonNode.Parse(json)!;

        Assert.True((bool)root["redacted"]!);
        Assert.Equal("song.flac", (string)root["engine"]!["sourcePath"]!);

        var evt = root["events"]![0]!;
        Assert.DoesNotContain("token=abc123", (string)evt["message"]!);
        Assert.DoesNotContain("Secret Album", (string)evt["message"]!);
        Assert.Equal("song.flac", (string)evt["data"]!["path"]!);

        Assert.DoesNotContain("bob", json);
        Assert.DoesNotContain("Secret Album", json);
        Assert.DoesNotContain("token=abc123", json);
        Assert.DoesNotContain("sig=zzz", json);
    }

    [Fact]
    public void ExportJson_keeps_original_values_when_sensitive_requested()
    {
        var json = DiagnosticExporter.ExportJson(CreateSnapshot(), includeSensitive: true);
        var root = JsonNode.Parse(json)!;

        Assert.False((bool)root["redacted"]!);
        Assert.Equal(FullPath, (string)root["engine"]!["sourcePath"]!);
        Assert.Contains("token=abc123", (string)root["events"]![0]!["message"]!);
    }

    [Fact]
    public void ExportJson_uses_camel_case_names_and_string_enums()
    {
        var json = DiagnosticExporter.ExportJson(CreateSnapshot(), includeSensitive: true);
        var root = JsonNode.Parse(json)!;

        Assert.NotNull(root["capturedAtUtc"]);
        Assert.Equal("error", (string)root["events"]![0]!["level"]!);
    }

    [Fact]
    public void ExportTextSummary_redacts_unless_sensitive()
    {
        var snapshot = CreateSnapshot();

        var redacted = DiagnosticExporter.ExportTextSummary(snapshot, includeSensitive: false);
        var full = DiagnosticExporter.ExportTextSummary(snapshot, includeSensitive: true);

        Assert.DoesNotContain("Secret Album", redacted);
        Assert.DoesNotContain("token=abc123", redacted);
        Assert.Contains("song.flac", redacted);
        Assert.Contains("Secret Album", full);
        Assert.Contains("[Error] playback.error", full);
    }

    [Fact]
    public void ExportTextSummary_lists_scenario_results_without_inventing_passes()
    {
        var snapshot = CreateSnapshot() with
        {
            Scenarios = new ScenarioReportDto
            {
                Results =
                [
                    new ScenarioResult { ScenarioId = "playback.basic" },
                    new ScenarioResult { ScenarioId = "gapless.two-track", Status = ScenarioStatus.Pass, Notes = "clean" },
                ],
            },
        };

        var text = DiagnosticExporter.ExportTextSummary(snapshot, includeSensitive: true);

        Assert.Contains("[NotRun] playback.basic", text);
        Assert.Contains("[Pass] gapless.two-track", text);
    }

    private static DiagnosticSnapshot CreateSnapshot() => new()
    {
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Environment = new EnvironmentInfoDto { Os = "TestOS", Runtime = ".NET", ProcessArchitecture = "X64", SonoraVersion = "1", TestPlayerVersion = "1" },
        Engine = new EngineSnapshotDto
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            HasSource = true,
            SourcePath = FullPath,
        },
        Events =
        [
            new DiagnosticEventDto(
                Seq: 1,
                ElapsedMs: 12.5,
                UtcTime: DateTimeOffset.UtcNow,
                Category: "playback",
                Level: DiagnosticLevel.Error,
                Name: "playback.error",
                Message: $"Failed to open {FullPath} via {SignedUrl}",
                Data: new Dictionary<string, string> { ["path"] = FullPath }),
        ],
        TotalEventsRecorded = 1,
    };
}
