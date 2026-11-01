namespace Sonora.Audio.Tests;

/// <summary>
/// Placeholder for long-running soak coverage. Excluded from CI via Category!=Soak.
/// </summary>
public sealed class SoakTests
{
    [Fact]
    [Trait("Category", "Soak")]
    public void Soak_Reserved_ForLongRunningStability()
    {
        // Intentionally minimal until a measured soak harness exists (see docs/performance-baseline.md).
        Assert.True(true);
    }
}
