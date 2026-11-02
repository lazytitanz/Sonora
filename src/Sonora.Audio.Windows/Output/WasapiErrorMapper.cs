using System.Runtime.InteropServices;
using Sonora.Audio.Output;

namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Maps Windows / NAudio exclusive-mode failures to Sonora failure kinds.
/// </summary>
internal static class WasapiErrorMapper
{
    // AUDCLNT_* from audioclient.h
    private const int AudClntEDeviceInUse = unchecked((int)0x8889000A);
    private const int AudClntEExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    private const int AudClntEUnsupportedFormat = unchecked((int)0x88890008);
    private const int AudClntEEndpointCreateFailed = unchecked((int)0x88890017);
    private const int AudClntEDeviceInvalidated = unchecked((int)0x88890004);
    private const int AudClntEBufferSizeNotAligned = unchecked((int)0x88890019);
    // AUDCLNT_E_BUFFER_SIZE_ERROR — exclusive ReleaseBuffer with a short/zero period.
    private const int AudClntEBufferSizeError = unchecked((int)0x88890016);
    private const int AudClntEInvalidDevicePeriod = unchecked((int)0x88890020);

    public static AudioOutputFailureKind Classify(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is AudioOutputException already)
            {
                return already.FailureKind;
            }

            if (ex is COMException com)
            {
                return ClassifyHResult(com.HResult);
            }

            // Some NAudio paths wrap HRESULT in plain Exception.HResult
            if (ex.HResult != 0)
            {
                var kind = ClassifyHResult(ex.HResult);
                if (kind != AudioOutputFailureKind.Unspecified)
                {
                    return kind;
                }
            }
        }

        var message = exception.Message ?? string.Empty;
        if (message.Contains("exclusive", StringComparison.OrdinalIgnoreCase)
            && message.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return AudioOutputFailureKind.ExclusiveModeRestricted;
        }

        if (message.Contains("in use", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DEVICE_IN_USE", StringComparison.OrdinalIgnoreCase))
        {
            return AudioOutputFailureKind.DeviceBusy;
        }

        if (message.Contains("format", StringComparison.OrdinalIgnoreCase)
            && message.Contains("support", StringComparison.OrdinalIgnoreCase))
        {
            return AudioOutputFailureKind.UnsupportedFormat;
        }

        return AudioOutputFailureKind.InitializationFailed;
    }

    public static AudioOutputFailureKind ClassifyHResult(int hresult) =>
        hresult switch
        {
            AudClntEDeviceInUse => AudioOutputFailureKind.DeviceBusy,
            AudClntEExclusiveModeNotAllowed => AudioOutputFailureKind.ExclusiveModeRestricted,
            AudClntEUnsupportedFormat => AudioOutputFailureKind.UnsupportedFormat,
            AudClntEDeviceInvalidated => AudioOutputFailureKind.DeviceUnavailable,
            AudClntEEndpointCreateFailed => AudioOutputFailureKind.DeviceUnavailable,
            AudClntEBufferSizeNotAligned => AudioOutputFailureKind.InitializationFailed,
            AudClntEBufferSizeError => AudioOutputFailureKind.InitializationFailed,
            AudClntEInvalidDevicePeriod => AudioOutputFailureKind.InitializationFailed,
            _ => AudioOutputFailureKind.Unspecified,
        };

    public static bool IsRecoverableBufferAlignmentFailure(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is COMException com
                && (com.HResult == AudClntEBufferSizeNotAligned
                    || com.HResult == AudClntEBufferSizeError
                    || com.HResult == AudClntEInvalidDevicePeriod))
            {
                return true;
            }

            if (ex.HResult == AudClntEBufferSizeNotAligned
                || ex.HResult == AudClntEBufferSizeError
                || ex.HResult == AudClntEInvalidDevicePeriod)
            {
                return true;
            }
        }

        return false;
    }

    public static AudioOutputException Wrap(string message, Exception inner)
    {
        var kind = Classify(inner);
        if (kind == AudioOutputFailureKind.Unspecified)
        {
            kind = AudioOutputFailureKind.InitializationFailed;
        }

        return new AudioOutputException(message, kind, inner);
    }
}
