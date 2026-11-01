using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioOutputFactory : IAudioOutputFactory
{
    private readonly Func<AudioOutputCreateRequest, CancellationToken, IAudioOutput> _factory;
    private readonly Func<AudioOutputCreateRequest, AudioFormat, CancellationToken, AudioOutputFormatProbeResult>? _probe;

    public FakeAudioOutputFactory(Func<IAudioOutput>? factory = null)
    {
        if (factory is null)
        {
            _factory = (req, _) => new FakeAudioOutput(endpointId: req.DeviceId ?? "default");
        }
        else
        {
            _factory = (_, _) => factory();
        }
    }

    public FakeAudioOutputFactory(Func<string?, IAudioOutput> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (req, _) => factory(req.DeviceId);
    }

    public FakeAudioOutputFactory(Func<string?, CancellationToken, IAudioOutput> factory)
    {
        _factory = (req, ct) => factory(req.DeviceId, ct);
    }

    public FakeAudioOutputFactory(Func<AudioOutputCreateRequest, CancellationToken, IAudioOutput> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public FakeAudioOutputFactory(
        Func<AudioOutputCreateRequest, CancellationToken, IAudioOutput> factory,
        Func<AudioOutputCreateRequest, AudioFormat, CancellationToken, AudioOutputFormatProbeResult> probe)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public List<(AudioOutputCreateRequest Request, IAudioOutput Output)> Created { get; } = new();

    public Task<IAudioOutput> CreateOutputAsync(
        string? deviceId = null,
        CancellationToken cancellationToken = default) =>
        CreateOutputAsync(AudioOutputCreateRequest.Shared(deviceId), cancellationToken);

    public Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = _factory(request, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Created.Add((request, output));
        return Task.FromResult(output);
    }

    public Task<AudioOutputFormatProbeResult> ProbeOutputFormatAsync(
        AudioOutputCreateRequest request,
        AudioFormat preferredSessionFormat,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_probe is not null)
        {
            return Task.FromResult(_probe(request, preferredSessionFormat, cancellationToken));
        }

        if (request.Mode != AudioOutputMode.Exclusive)
        {
            return Task.FromResult(new AudioOutputFormatProbeResult(true, preferredSessionFormat));
        }

        // Default fake: exclusive is supported at the preferred session format.
        return Task.FromResult(new AudioOutputFormatProbeResult(true, preferredSessionFormat));
    }
}
