using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioOutputFactory : IAudioOutputFactory
{
    private readonly Func<string?, IAudioOutput> _factory;

    public FakeAudioOutputFactory(Func<IAudioOutput>? factory = null)
    {
        if (factory is null)
        {
            _factory = id => new FakeAudioOutput(endpointId: id ?? "default");
        }
        else
        {
            _factory = _ => factory();
        }
    }

    public FakeAudioOutputFactory(Func<string?, IAudioOutput> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public List<(string? DeviceId, IAudioOutput Output)> Created { get; } = new();

    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = _factory(deviceId);
        Created.Add((deviceId, output));
        return Task.FromResult(output);
    }
}
