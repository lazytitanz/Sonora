namespace Sonora.Audio.Diagnostics;

/// <summary>
/// Why the output pull path returned silence (or non-advancing PCM). Used to separate legitimate
/// seek/switch/EOS windows from indefinite Playing+silent stalls.
/// </summary>
public enum SeekSilenceReason : short
{
    None = 0,
    EffectsBarrier = 1,
    OutputSwitching = 2,
    EndOfStream = 3,
    Underrun = 4,
    PendingNext = 5,
    Seeking = 6,
    Buffering = 7,
}
