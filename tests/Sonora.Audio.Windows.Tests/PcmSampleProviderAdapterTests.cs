using Sonora.Audio.Abstractions;
using Sonora.Audio.Formats;
using Sonora.Audio.Windows.Output;

namespace Sonora.Audio.Windows.Tests;

public sealed class PcmSampleProviderAdapterTests
{
    private static readonly AudioFormat Format = AudioFormat.CreateFloat32(44100, 2);

    [Fact]
    public void Short_final_read_is_padded_to_full_period()
    {
        var source = new ScriptedSource(Format, [3, 0]);
        var adapter = new PcmSampleProviderAdapter(source);
        Span<float> buffer = stackalloc float[8];
        buffer.Fill(1f);

        var read = adapter.Read(buffer);

        Assert.Equal(8, read);
        Assert.Equal(0f, buffer[0]); // scripted source writes zeros then we pad
        Assert.Equal(0f, buffer[7]);
    }

    [Fact]
    public void After_end_of_stream_padding_subsequent_reads_return_silence()
    {
        var eos = 0;
        var source = new ScriptedSource(Format, [2]);
        source.MarkEndedAfterReads = 1;
        var adapter = new PcmSampleProviderAdapter(source, () => Interlocked.Increment(ref eos));
        Span<float> buffer = stackalloc float[8];

        Assert.Equal(8, adapter.Read(buffer));
        Assert.Equal(1, Volatile.Read(ref eos));
        Assert.Equal(8, adapter.Read(buffer));
        Assert.All(buffer.ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(1, Volatile.Read(ref eos)); // callback once
    }

    [Fact]
    public void Already_ended_source_returns_silence_and_raises_eos_once()
    {
        var eos = 0;
        var source = new ScriptedSource(Format, []) { IsEndOfStream = true };
        var adapter = new PcmSampleProviderAdapter(source, () => Interlocked.Increment(ref eos));
        Span<float> buffer = stackalloc float[4];
        buffer.Fill(0.5f);

        Assert.Equal(4, adapter.Read(buffer));
        Assert.All(buffer.ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(1, Volatile.Read(ref eos));
        Assert.Equal(4, adapter.Read(buffer));
        Assert.Equal(1, Volatile.Read(ref eos));
    }

    [Fact]
    public void Short_read_without_eos_is_padded_but_does_not_latch_eos()
    {
        var eos = 0;
        // One short read, source remains open (underrun-like).
        var source = new ScriptedSource(Format, [3]) { MarkEndedAfterReads = 0, EndWhenQueueEmpty = false };
        var adapter = new PcmSampleProviderAdapter(source, () => Interlocked.Increment(ref eos));
        Span<float> buffer = stackalloc float[8];

        Assert.Equal(8, adapter.Read(buffer));
        Assert.Equal(0, Volatile.Read(ref eos));
        Assert.False(adapter.HasSignaledEndOfStream);
    }

    [Fact]
    public void SetPaused_returns_silence_without_consuming_source()
    {
        var source = new ScriptedSource(Format, [8, 8]);
        var adapter = new PcmSampleProviderAdapter(source);
        Span<float> buffer = stackalloc float[8];

        adapter.SetPaused(true);
        Assert.Equal(8, adapter.Read(buffer));
        Assert.All(buffer.ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(2, source.RemainingReads);

        adapter.SetPaused(false);
        Assert.Equal(8, adapter.Read(buffer));
        Assert.Equal(1, source.RemainingReads);
    }

    [Fact]
    public void ResetEndOfStreamLatch_allows_reads_again()
    {
        var source = new ScriptedSource(Format, []) { IsEndOfStream = true };
        var adapter = new PcmSampleProviderAdapter(source);
        Span<float> buffer = stackalloc float[4];
        Assert.Equal(4, adapter.Read(buffer));
        Assert.True(adapter.HasSignaledEndOfStream);

        source.IsEndOfStream = false;
        source.Enqueue(4);
        adapter.ResetEndOfStreamLatch();
        Assert.False(adapter.HasSignaledEndOfStream);
        Assert.Equal(4, adapter.Read(buffer));
        Assert.False(adapter.HasSignaledEndOfStream);
    }

    [Fact]
    public void ManagedOutputCapture_skipsThenCapturesExactWindow()
    {
        var source = new FillingSource(Format, fillValue: 0.25f);
        var adapter = new PcmSampleProviderAdapter(source);
        adapter.ArmManagedOutputCapture(skipSamples: 5, captureSamples: 4);

        Span<float> period = stackalloc float[3];
        Assert.Equal(3, adapter.Read(period)); // skip 3
        Assert.False(adapter.ManagedOutputCapture!.IsComplete);
        Assert.Equal(3, adapter.Read(period)); // skip 2, capture 1
        Assert.Equal(3, adapter.Read(period)); // capture 3 → complete
        Assert.True(adapter.ManagedOutputCapture.IsComplete);

        Span<float> captured = stackalloc float[4];
        Assert.True(adapter.ManagedOutputCapture.TryCopyCaptured(captured, out var written));
        Assert.Equal(4, written);
        Assert.All(captured.ToArray(), s => Assert.Equal(0.25f, s));
    }

    private sealed class FillingSource : IPcmSampleSource
    {
        private readonly float _fillValue;

        public FillingSource(AudioFormat format, float fillValue)
        {
            Format = format;
            _fillValue = fillValue;
        }

        public AudioFormat Format { get; }
        public bool IsEndOfStream => false;

        public int Read(Span<float> destination)
        {
            destination.Fill(_fillValue);
            return destination.Length;
        }
    }

    private sealed class ScriptedSource : IPcmSampleSource
    {
        private readonly Queue<int> _reads;

        public ScriptedSource(AudioFormat format, IEnumerable<int> reads)
        {
            Format = format;
            _reads = new Queue<int>(reads);
        }

        public AudioFormat Format { get; }
        public bool IsEndOfStream { get; set; }
        public int MarkEndedAfterReads { get; set; }
        public bool EndWhenQueueEmpty { get; set; } = true;
        public int RemainingReads => _reads.Count;

        public void Enqueue(int samples) => _reads.Enqueue(samples);

        public int Read(Span<float> destination)
        {
            if (IsEndOfStream)
            {
                return 0;
            }

            if (_reads.Count == 0)
            {
                if (EndWhenQueueEmpty)
                {
                    IsEndOfStream = true;
                }

                return 0;
            }

            var n = Math.Min(_reads.Dequeue(), destination.Length);
            destination[..n].Clear();
            if (MarkEndedAfterReads > 0)
            {
                MarkEndedAfterReads--;
                if (MarkEndedAfterReads == 0)
                {
                    IsEndOfStream = true;
                }
            }

            if (EndWhenQueueEmpty && _reads.Count == 0)
            {
                IsEndOfStream = true;
            }

            return n;
        }
    }
}
