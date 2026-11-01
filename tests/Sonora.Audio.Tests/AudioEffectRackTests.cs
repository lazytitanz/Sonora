using Sonora.Audio.DSP;
using Sonora.Audio.Formats;
using Sonora.Audio.Playback;
using Sonora.Audio.Tests.Fakes;

namespace Sonora.Audio.Tests;

public sealed class AudioEffectRackTests
{
    [Fact]
    public void AddEffects_DefaultOrder_PreampEqualizerLimiter()
    {
        var rack = new AudioEffectRack(new FakeAudioEffectFactory());
        var pre = rack.AddPreamp(-3f);
        var eq = rack.AddEqualizer();
        var lim = rack.AddLimiter();

        Assert.Equal(new IAudioEffect[] { pre, eq, lim }, rack.Items);
        Assert.Equal(6, eq.BandCount);
    }

    [Fact]
    public void Process_AppliesEffectsInOrder()
    {
        var rack = new AudioEffectRack(new MultiplyFactory());
        rack.Configure(AudioFormat.CreateFloat32(44100, 1));
        rack.AddPreamp(2f); // ×2
        rack.AddPreamp(3f); // ×3
        var buffer = new float[] { 1f };
        rack.Process(buffer, AudioFormat.CreateFloat32(44100, 1));
        Assert.Equal(6f, buffer[0], precision: 3);
    }

    [Fact]
    public void Bypass_SkipsProcessing()
    {
        var rack = new AudioEffectRack(new MultiplyFactory());
        rack.Configure(AudioFormat.CreateFloat32(44100, 1));
        rack.AddPreamp(2f);
        rack.Bypass = true;
        var buffer = new float[] { 1f };
        rack.Process(buffer, AudioFormat.CreateFloat32(44100, 1));
        Assert.Equal(1f, buffer[0]);
    }

    [Fact]
    public void Remove_DefersDisposeUntilDisposeRetired()
    {
        var rack = new AudioEffectRack(new FakeAudioEffectFactory());
        var pre = (FakePreampEffect)rack.AddPreamp(0);
        Assert.True(rack.Remove(pre));
        Assert.False(pre.Disposed);
        rack.DisposeRetired();
        Assert.True(pre.Disposed);
        Assert.Empty(rack.Items);
    }

    [Fact]
    public void Move_ReordersSnapshot()
    {
        var rack = new AudioEffectRack(new FakeAudioEffectFactory());
        var a = rack.AddPreamp(0);
        var b = rack.AddEqualizer();
        rack.Move(0, 1);
        Assert.Equal(new IAudioEffect[] { b, a }, rack.Items);
    }

    [Fact]
    public void IndependentPlayers_HaveIndependentRacks()
    {
        var options = new AudioEngineOptions { EffectFactory = new FakeAudioEffectFactory() };
        var decoder = FakeAudioDecoder.CreateSilence(AudioFormat.CreateFloat32(44100, 2), TimeSpan.FromSeconds(1));
        using var engine = new AudioEngine(new FakeAudioDecoderFactory(decoder), new FakeAudioOutputFactory(), options);
        using var p1 = engine.CreatePlayer();
        using var p2 = engine.CreatePlayer();
        p1.Effects.AddPreamp(3f);
        Assert.Empty(p2.Effects.Items);
        Assert.Single(p1.Effects.Items);
    }

    [Fact]
    public void Process_PreservesSampleCount()
    {
        var rack = new AudioEffectRack(new FakeAudioEffectFactory());
        rack.Configure(AudioFormat.CreateFloat32(48000, 2));
        rack.AddPreamp(6f);
        rack.AddEqualizer();
        rack.AddLimiter();
        var buffer = new float[256];
        Array.Fill(buffer, 0.1f);
        rack.Process(buffer, AudioFormat.CreateFloat32(48000, 2));
        Assert.Equal(256, buffer.Length);
    }

    private sealed class MultiplyFactory : IAudioEffectFactory
    {
        public IEqualizerEffect CreateEqualizer(EqualizerOptions options)
            => new FakeEqualizerEffect(options);

        public IPreampEffect CreatePreamp(float gainDb)
            => new MultiplyPreamp(gainDb);

        public ILimiterEffect CreateLimiter(LimiterOptions options)
            => new FakeLimiterEffect(options);
    }

    private sealed class MultiplyPreamp : FakeEffectBase, IPreampEffect
    {
        public MultiplyPreamp(float gainDb) => GainDb = gainDb;
        public override string Name => "Mul";
        public float GainDb { get; set; }

        protected override void ProcessCore(Span<float> buffer, in AudioFormat format)
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] *= GainDb;
            }
        }
    }

}
