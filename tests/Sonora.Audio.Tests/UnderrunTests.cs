using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class UnderrunTests
{
    [Fact]
    public async Task EmptyProducer_RaisesUnderrunWhilePlaying()
    {
        var format = AudioFormat.CreateFloat32(44100, 1);
        // Decoder returns a tiny preamble then blocks until cancelled, starving the ring.
        var decoder = new StallingDecoder(format, preambleSamples: 64);
        var output = new PullOnceOutput();
        using var engine = new AudioEngine(
            new FakeAudioDecoderFactory(decoder),
            new FakeAudioOutputFactory(() => output),
            new Playback.AudioEngineOptions
            {
                BufferDuration = TimeSpan.FromMilliseconds(20),
                PrimeDuration = TimeSpan.Zero,
            });
        using var player = engine.CreatePlayer();

        var underruns = 0;
        player.Underrun += (_, _) => Interlocked.Increment(ref underruns);

        await player.LoadAsync("stall.wav");
        player.Play();

        // Drain any preamble, then force empty reads from the ring.
        for (var i = 0; i < 20; i++)
        {
            output.Pull(512);
            await Task.Delay(10);
        }

        player.Stop();
        Assert.True(underruns >= 1, $"Expected underruns, got {underruns}. Diagnostics={player.GetDiagnostics().UnderrunCount}");
    }

    private sealed class StallingDecoder : IAudioDecoder
    {
        private readonly float[] _preamble;
        private int _offset;
        private readonly ManualResetEventSlim _block = new(false);
        private bool _disposed;

        public StallingDecoder(AudioFormat format, int preambleSamples)
        {
            Format = format;
            _preamble = new float[preambleSamples];
            Duration = format.SamplesToTime(preambleSamples);
            CanSeek = false;
        }

        public AudioFormat Format { get; }
        public TimeSpan Duration { get; }
        public bool CanSeek { get; }

        public int Read(Span<float> destination)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_offset < _preamble.Length)
            {
                var n = Math.Min(destination.Length, _preamble.Length - _offset);
                _preamble.AsSpan(_offset, n).CopyTo(destination);
                _offset += n;
                return n;
            }

            // Block the pump without signaling EOF (returning 0 would mark the ring completed).
            try
            {
                while (!_disposed)
                {
                    if (_block.Wait(50))
                    {
                        break;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }

            return 0;
        }

        public void Seek(TimeSpan position) => throw new NotSupportedException();

        public void Dispose()
        {
            _disposed = true;
            _block.Set();
            _block.Dispose();
        }
    }

    private sealed class PullOnceOutput : IAudioOutput
    {
        private IPcmSampleSource? _source;

        public AudioFormat? Format { get; private set; }
        public TimeSpan AverageLatency => TimeSpan.FromMilliseconds(10);
        public TimeSpan CurrentLatency => AverageLatency;
        public event EventHandler<AudioOutputStoppedEventArgs>? Stopped;

        public void Initialize(IPcmSampleSource source)
        {
            _source = source;
            Format = source.Format;
        }

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Stop()
        {
        }

        public TimeSpan? TryGetRenderedPosition() => null;

        public void Pull(int samples)
        {
            var buffer = new float[samples];
            _ = _source!.Read(buffer);
        }

        public void Dispose()
        {
            Stopped?.Invoke(this, new AudioOutputStoppedEventArgs(false));
        }
    }
}
