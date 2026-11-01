using Sonora.Audio.Windows.Output;
using Xunit.Sdk;

namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Detects whether WASAPI shared-mode output can be created on this host.
/// GitHub-hosted runners often lack a usable render device.
/// </summary>
internal static class WindowsAudioEnvironment
{
    private static readonly Lazy<bool> CanCreateWasapiOutput = new(DetectWasapi, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool HasWasapiOutput => CanCreateWasapiOutput.Value;

    public static void SkipIfNoWasapiOutput()
    {
        if (!HasWasapiOutput)
        {
            throw SkipException.ForSkip(
                "WASAPI shared-mode output is unavailable (common on CI runners without an audio endpoint).");
        }
    }

    private static bool DetectWasapi()
    {
        try
        {
            using var output = new WindowsAudioOutputFactory().CreateDefaultOutput();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
