using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioOutputFactory : IAudioOutputFactory
{
    private readonly Func<string?, CancellationToken, IAudioOutput> _factory;

    public FakeAudioOutputFactory(Func<IAudioOutput>? factory = null)
    {
        if (factory is null)
        {
            _factory = (id, _) => new FakeAudioOutput(endpointId: id ?? "default");
        }
        else
        {
            _factory = (_, _) => factory();
        }
    }

    public FakeAudioOutputFactory(Func<string?, IAudioOutput> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (id, _) => factory(id);
    }

    public FakeAudioOutputFactory(Func<string?, CancellationToken, IAudioOutput> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public List<(string? DeviceId, IAudioOutput Output)> Created { get; } = new();

    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = _factory(deviceId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Created.Add((deviceId, output));
        return Task.FromResult(output);
    }
}
