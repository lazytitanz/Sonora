using System.Diagnostics;
using Sonora.Audio.Buffers;
using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Metadata;
using Sonora.Audio.Windows.DSP;

static long AllocatedBytes()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    return GC.GetAllocatedBytesForCurrentThread();
}

static void Report(string name, int iterations, Stopwatch sw, long allocBefore, long allocAfter)
{
    var ms = sw.Elapsed.TotalMilliseconds;
    var alloc = Math.Max(0, allocAfter - allocBefore);
    Console.WriteLine(
        $"{name}: {iterations} iters in {ms:F2} ms ({iterations / (ms / 1000.0):F0}/s), alloc≈{alloc} B ({alloc / (double)iterations:F1} B/iter)");
}

var format = AudioFormat.CreateFloat32(48000, 2);
var frames = 256;
var samples = frames * format.ChannelCount;
Console.WriteLine($"Sonora benchmarks — {Environment.OSVersion} / {Environment.Version}");
Console.WriteLine($"Machine={Environment.MachineName}, Processors={Environment.ProcessorCount}");
Console.WriteLine($"Format {format.SampleRate} Hz × {format.ChannelCount} ch, block={frames} frames");
Console.WriteLine($"Config=Release, Sonora=1.0.0-rc.1");
Console.WriteLine();

// Ring buffer throughput
{
    var ring = new PcmRingBuffer(samples * 64);
    var block = new float[samples];
    var readBuf = new float[samples];
    for (var i = 0; i < block.Length; i++)
    {
        block[i] = 0.1f;
    }

    const int iters = 50_000;
    for (var i = 0; i < 100; i++)
    {
        _ = ring.Write(block, CancellationToken.None);
        _ = ring.TryRead(readBuf);
    }

    var before = AllocatedBytes();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iters; i++)
    {
        _ = ring.Write(block, CancellationToken.None);
        _ = ring.TryRead(readBuf);
    }

    sw.Stop();
    Report("PcmRingBuffer write+read", iters, sw, before, AllocatedBytes());
    ring.Dispose();
}

// Effect rack (preamp → EQ → limiter)
{
    var factory = new WindowsAudioEffectFactory();
    var rack = new AudioEffectRack(factory);
    rack.Configure(format);
    rack.AddPreamp(0f);
    rack.AddEqualizer(EqualizerOptions.CreateSixBand());
    rack.AddLimiter(new LimiterOptions());

    var buffer = new float[samples];
    for (var i = 0; i < buffer.Length; i++)
    {
        buffer[i] = 0.1f * MathF.Sin(2 * MathF.PI * 440f * i / format.SampleRate);
    }

    for (var i = 0; i < 1000; i++)
    {
        rack.Process(buffer, format);
    }

    const int iters = 100_000;
    var before = AllocatedBytes();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iters; i++)
    {
        rack.Process(buffer, format);
    }

    sw.Stop();
    Report("DSP rack Process (preamp+EQ+limiter)", iters, sw, before, AllocatedBytes());
    rack.Dispose();
}

// Limiter alone
{
    var lim = new NAudioLimiterEffect(new LimiterOptions());
    lim.Configure(format);
    lim.ApplyPendingUpdates();
    var buffer = new float[samples];
    for (var i = 0; i < buffer.Length; i++)
    {
        buffer[i] = 0.5f;
    }

    for (var i = 0; i < 1000; i++)
    {
        lim.Process(buffer, format);
    }

    const int iters = 200_000;
    var before = AllocatedBytes();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iters; i++)
    {
        lim.Process(buffer, format);
    }

    sw.Stop();
    Report("Limiter Process alone", iters, sw, before, AllocatedBytes());
    lim.Dispose();
}

// Metadata scan
{
    var tmp = Path.Combine(Path.GetTempPath(), "sonora-bench-tone.wav");
    WriteSilentWav(tmp, 44100, 2, TimeSpan.FromMilliseconds(50));
    var reader = new AudioMetadataReader();
    const int iters = 200;
    _ = reader.Read(tmp);
    var before = AllocatedBytes();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iters; i++)
    {
        _ = reader.Read(tmp);
    }

    sw.Stop();
    Report("AudioMetadataReader.Read (tiny WAV)", iters, sw, before, AllocatedBytes());
    try
    {
        File.Delete(tmp);
    }
    catch
    {
        // ignored
    }
}

Console.WriteLine();
Console.WriteLine("Done.");

static void WriteSilentWav(string path, int sampleRate, int channels, TimeSpan duration)
{
    var frameCount = (int)(sampleRate * duration.TotalSeconds);
    var dataBytes = frameCount * channels * 2;
    using var fs = File.Create(path);
    using var bw = new BinaryWriter(fs);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
    bw.Write(36 + dataBytes);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
    bw.Write(16);
    bw.Write((short)1);
    bw.Write((short)channels);
    bw.Write(sampleRate);
    bw.Write(sampleRate * channels * 2);
    bw.Write((short)(channels * 2));
    bw.Write((short)16);
    bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
    bw.Write(dataBytes);
    bw.Write(new byte[dataBytes]);
}
