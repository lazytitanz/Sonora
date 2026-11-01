namespace Sonora.Audio.Windows.Tests;

/// <summary>
/// Shared gate for WASAPI / device-dependent tests.
/// CI excludes <c>Category=Hardware</c>; local runs also skip unless
/// <c>SONORA_RUN_HARDWARE_TESTS=1</c> so accidental <c>dotnet test</c> is silent and safe.
/// </summary>
internal static class HardwareTestGate
{
    public const string EnvironmentVariableName = "SONORA_RUN_HARDWARE_TESTS";

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariableName), "1", StringComparison.Ordinal);

    public static string SkipReason =>
        $"Hardware tests require {EnvironmentVariableName}=1. See docs/hardware-testing.md.";
}

/// <summary>
/// Fact that skips unless <c>SONORA_RUN_HARDWARE_TESTS=1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            Skip = HardwareTestGate.SkipReason;
        }
    }
}

/// <summary>
/// Theory that skips unless <c>SONORA_RUN_HARDWARE_TESTS=1</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareTheoryAttribute : TheoryAttribute
{
    public HardwareTheoryAttribute()
    {
        if (!HardwareTestGate.IsEnabled)
        {
            Skip = HardwareTestGate.SkipReason;
        }
    }
}
