using ATL;

namespace Sonora.Audio.Metadata;

/// <summary>
/// Documents and applies the minimal ATL process-global settings Sonora requires.
/// ATL 7.19.0 does not expose isolated per-call configuration.
/// </summary>
/// <remarks>
/// <para>
/// Sonora prefers normalizing returned values in <see cref="AtlMetadataMapper"/>
/// (empty strings → null, non-positive track/year → null) without mutating ATL settings.
/// </para>
/// <para>
/// The one unavoidable setting is <see cref="Settings.UseFileNameWhenNoTitle"/>.
/// ATL defaults it to <see langword="true"/>, which invents a title from the file name.
/// Sonora requires titles to remain <see langword="null"/> when tags omit them, so this
/// policy sets <c>UseFileNameWhenNoTitle = false</c> once on first metadata use.
/// </para>
/// <para>
/// Implications for applications that also use ATL directly in the same process:
/// after the first Sonora metadata read, ATL will no longer substitute file names for
/// missing titles. Applications that relied on that ATL default should supply their own
/// fallback. Sonora does not restore the previous value and does not mutate any other
/// ATL settings (including <c>NullAbsentValues</c>, <c>DisplayValueSeparator</c>,
/// and <c>MP3_parseExactDuration</c>).
/// </para>
/// </remarks>
internal static class AtlSettingsPolicy
{
    private static int _initialized;

    /// <summary>
    /// Ensures Sonora-required ATL settings are applied exactly once per process.
    /// </summary>
    public static void EnsureApplied()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) != 0)
        {
            return;
        }

        // Only setting Sonora changes. See type remarks.
        Settings.UseFileNameWhenNoTitle = false;
    }
}
