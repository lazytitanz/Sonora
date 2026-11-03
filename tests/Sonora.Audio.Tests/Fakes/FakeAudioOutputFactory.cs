using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Output;

namespace Sonora.Audio.Tests.Fakes;

internal sealed class FakeAudioOutputFactory : IAudioOutputFactory
{
    private readonly Func<AudioOutputCreateRequest, CancellationToken, Task<IAudioOutput>> _factory;
    private readonly Func<AudioOutputCreateRequest, AudioFormat, CancellationToken, AudioOutputFormatProbeResult>? _probe;

    public FakeAudioOutputFactory(Func<IAudioOutput>? factory = null)
    {
        if (factory is null)
        {
            _factory = (req, _) =>
                Task.FromResult<IAudioOutput>(new FakeAudioOutput(endpointId: req.DeviceId ?? "default"));
        }
        else
        {
            _factory = (_, _) => Task.FromResult(factory());
        }
    }

    public FakeAudioOutputFactory(Func<string?, IAudioOutput> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (req, _) => Task.FromResult(factory(req.DeviceId));
    }

    public FakeAudioOutputFactory(Func<string?, CancellationToken, IAudioOutput> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (req, ct) => Task.FromResult(factory(req.DeviceId, ct));
    }

    /// <summary>
    /// Async create path — prefer this when the factory must wait for cancellation so the
    /// thread pool is not blocked (sync Sleep loops deadlock under parallel CI load).
    /// </summary>
    public FakeAudioOutputFactory(Func<string?, CancellationToken, Task<IAudioOutput>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (req, ct) => factory(req.DeviceId, ct);
    }

    public FakeAudioOutputFactory(Func<AudioOutputCreateRequest, CancellationToken, IAudioOutput> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (req, ct) => Task.FromResult(factory(req, ct));
    }

    public FakeAudioOutputFactory(Func<AudioOutputCreateRequest, CancellationToken, Task<IAudioOutput>> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public FakeAudioOutputFactory(
        Func<AudioOutputCreateRequest, CancellationToken, IAudioOutput> factory,
        Func<AudioOutputCreateRequest, AudioFormat, CancellationToken, AudioOutputFormatProbeResult> probe)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = (req, ct) => Task.FromResult(factory(req, ct));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    }

    public FakeAudioOutputFactory(
        Func<AudioOutputCreateRequest, CancellationToken, Task<IAudioOutput>> factory,
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

    public async Task<IAudioOutput> CreateOutputAsync(
        AudioOutputCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = await _factory(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Created.Add((request, output));
        return output;
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
