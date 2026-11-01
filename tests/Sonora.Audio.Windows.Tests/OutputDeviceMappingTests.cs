using NAudio.CoreAudioApi;
using Sonora.Audio.Devices;
using Sonora.Audio.Windows.Devices;

namespace Sonora.Audio.Windows.Tests;

public class OutputDeviceMappingTests
{
    [Theory]
    [InlineData(DeviceState.Active, AudioDeviceState.Active)]
    [InlineData(DeviceState.Disabled, AudioDeviceState.Disabled)]
    [InlineData(DeviceState.Unplugged, AudioDeviceState.Unplugged)]
    [InlineData(DeviceState.NotPresent, AudioDeviceState.NotPresent)]
    public void MapState_MatchesCoreAudio(DeviceState source, AudioDeviceState expected)
    {
        Assert.Equal(expected, WindowsAudioOutputDevices.MapState(source));
    }

    [Fact]
    public void Enumerator_ReturnsRenderDevices_WithoutThrowing()
    {
        using var devices = new WindowsAudioOutputDevices();
        var list = devices.GetDevices();
        Assert.NotNull(list);
        // CI machines always have at least a render endpoint enumeration path;
        // the list may be empty only in exotic headless configs.
        foreach (var device in list)
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id));
            Assert.False(string.IsNullOrWhiteSpace(device.Name));
        }
    }
}
