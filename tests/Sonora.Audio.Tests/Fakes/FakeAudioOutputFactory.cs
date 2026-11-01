using Sonora.Audio.Abstractions;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioOutputFactory : IAudioOutputFactory
{
    private readonly Func<IAudioOutput> _factory;

    public FakeAudioOutputFactory(Func<IAudioOutput>? factory = null)
    {
        _factory = factory ?? (() => new FakeAudioOutput());
    }

    public List<IAudioOutput> Created { get; } = new();

    public IAudioOutput CreateDefaultOutput()
    {
        var output = _factory();
        Created.Add(output);
        return output;
    }
}
