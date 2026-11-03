using Sonora.TestPlayer.Diagnostics;

namespace Sonora.TestPlayer.Tests;

public sealed class ListeningPathRulesTests
{
    [Fact]
    public void VerifiedPromote_Requires_Transition_And_Matching_B()
    {
        var b = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "track-b.wav"));
        Assert.True(ListeningPathRules.IsVerifiedPromote(true, 3, b, b));
        Assert.False(ListeningPathRules.IsVerifiedPromote(false, 3, b, b));
        Assert.False(ListeningPathRules.IsVerifiedPromote(true, 0, b, b));
        Assert.False(ListeningPathRules.IsVerifiedPromote(true, 3, b, Path.Combine(Path.GetTempPath(), "other.wav")));
    }

    [Fact]
    public void VerifiedFreshLoad_Requires_No_Transition()
    {
        var b = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "track-b.wav"));
        Assert.True(ListeningPathRules.IsVerifiedFreshLoad(false, b, b));
        Assert.False(ListeningPathRules.IsVerifiedFreshLoad(true, b, b));
    }

    [Fact]
    public void SummarizePath_Promote_Includes_TransitionId()
    {
        var s = ListeningPathRules.SummarizePath(
            "Natural promotion",
            trackTransitionedFired: true,
            transitionId: 2,
            ListeningClassification.Static,
            "PCM exact");
        Assert.Equal("Natural promotion: Static / PCM exact (TrackTransitioned id=2)", s);
    }

    [Fact]
    public void SummarizePath_Load_Notes_No_Transition()
    {
        var s = ListeningPathRules.SummarizePath(
            "Fresh Load",
            trackTransitionedFired: false,
            transitionId: 0,
            ListeningClassification.Clean,
            "PCM inconclusive");
        Assert.Equal("Fresh Load: Clean / PCM inconclusive (no TrackTransitioned)", s);
    }
}
