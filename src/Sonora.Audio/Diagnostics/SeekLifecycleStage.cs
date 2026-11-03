namespace Sonora.Audio.Diagnostics;

/// <summary>Stages recorded in the seek / playback lifecycle trace ring.</summary>
public enum SeekLifecycleStage : short
{
    ControllerEnter = 1,
    ControllerCoalesced = 2,
    SeekBegin = 3,
    ArmBarrier = 4,
    WaitBarrier = 5,
    StopPumpEnter = 6,
    StopPumpExit = 7,
    StopPumpTimeout = 8,
    DecoderSeekEnter = 9,
    DecoderSeekExit = 10,
    Prime = 11,
    Commit = 12,
    ExitBarrier = 13,
    Superseded = 14,
    Failed = 15,
    RestoreAfterFail = 16,
    Promote = 17,
    CallbackHeartbeat = 18,
    ReadSilence = 19,
    DecodeWorkerAliveAfterStop = 20,
    SeekOverlapEnter = 21,
    SeekOverlapExit = 22,
    /// <summary>Cleared sticky EOS and restarted output because next was Ready/Loading.</summary>
    GaplessRecover = 23,
}
