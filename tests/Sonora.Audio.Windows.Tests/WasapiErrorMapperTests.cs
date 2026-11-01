using System.Runtime.InteropServices;
using Sonora.Audio.Output;
using Sonora.Audio.Windows.Output;

namespace Sonora.Audio.Windows.Tests;

public class WasapiErrorMapperTests
{
    private const int AudClntEDeviceInUse = unchecked((int)0x8889000A);
    private const int AudClntEExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    private const int AudClntEUnsupportedFormat = unchecked((int)0x88890008);
    private const int AudClntEBufferSizeNotAligned = unchecked((int)0x88890019);

    [Fact]
    public void Classify_DeviceInUse_IsBusy()
    {
        var ex = new COMException("in use", AudClntEDeviceInUse);
        Assert.Equal(AudioOutputFailureKind.DeviceBusy, WasapiErrorMapper.Classify(ex));
    }

    [Fact]
    public void Classify_ExclusiveNotAllowed_IsRestricted()
    {
        var ex = new COMException("not allowed", AudClntEExclusiveModeNotAllowed);
        Assert.Equal(AudioOutputFailureKind.ExclusiveModeRestricted, WasapiErrorMapper.Classify(ex));
    }

    [Fact]
    public void Classify_UnsupportedFormat()
    {
        var ex = new COMException("format", AudClntEUnsupportedFormat);
        Assert.Equal(AudioOutputFailureKind.UnsupportedFormat, WasapiErrorMapper.Classify(ex));
    }

    [Fact]
    public void BufferAlignment_IsRecoverable()
    {
        var ex = new COMException("align", AudClntEBufferSizeNotAligned);
        Assert.True(WasapiErrorMapper.IsRecoverableBufferAlignmentFailure(ex));
        Assert.Equal(AudioOutputFailureKind.InitializationFailed, WasapiErrorMapper.Classify(ex));
    }

    [Fact]
    public void Wrap_PreservesKind()
    {
        var inner = new COMException("busy", AudClntEDeviceInUse);
        var wrapped = WasapiErrorMapper.Wrap("failed", inner);
        Assert.Equal(AudioOutputFailureKind.DeviceBusy, wrapped.FailureKind);
    }
}
