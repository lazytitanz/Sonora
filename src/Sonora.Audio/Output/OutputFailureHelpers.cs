namespace Sonora.Audio.Output;

/// <summary>Shared classification helpers for exclusive fallback decisions.</summary>
internal static class OutputFailureHelpers
{
    public static ExclusiveFallbackReason ToFallbackReason(AudioOutputFailureKind kind) =>
        kind switch
        {
            AudioOutputFailureKind.DeviceBusy => ExclusiveFallbackReason.DeviceBusy,
            AudioOutputFailureKind.ExclusiveModeRestricted => ExclusiveFallbackReason.ExclusiveModeRestricted,
            AudioOutputFailureKind.UnsupportedFormat => ExclusiveFallbackReason.UnsupportedFormat,
            AudioOutputFailureKind.InitializationFailed => ExclusiveFallbackReason.InitializationFailed,
            _ => ExclusiveFallbackReason.InitializationFailed,
        };

    public static bool IsFallbackEligible(AudioOutputFailureKind kind) =>
        kind is AudioOutputFailureKind.DeviceBusy
            or AudioOutputFailureKind.ExclusiveModeRestricted
            or AudioOutputFailureKind.UnsupportedFormat
            or AudioOutputFailureKind.InitializationFailed;

    public static AudioOutputFailureKind KindOf(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is AudioOutputException output
                && output.FailureKind != AudioOutputFailureKind.Unspecified)
            {
                return output.FailureKind;
            }
        }

        return AudioOutputFailureKind.Unspecified;
    }
}
