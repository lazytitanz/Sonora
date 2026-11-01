using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioDecoderFactory : IAudioDecoderFactory
{
    private readonly Func<string, CancellationToken, Task<IAudioDecoder>> _factory;

    public FakeAudioDecoderFactory(Func<string, CancellationToken, Task<IAudioDecoder>> factory)
    {
        _factory = factory;
    }

    public FakeAudioDecoderFactory(IAudioDecoder decoder)
        : this((_, _) => Task.FromResult(decoder))
    {
    }

    public List<string> RequestedPaths { get; } = new();

    public Task<IAudioDecoder> CreateAsync(string filePath, CancellationToken cancellationToken = default)
    {
        RequestedPaths.Add(filePath);
        return _factory(filePath, cancellationToken);
    }
}
