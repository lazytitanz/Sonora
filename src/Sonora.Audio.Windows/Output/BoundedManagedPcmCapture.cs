namespace Sonora.Audio.Windows.Output;

/// <summary>
/// Single-writer bounded capture of interleaved float PCM for diagnostic probes.
/// Used by managed-provider and render-submission captures; callers must label which boundary.
/// Arm off the audio callback; <see cref="Offer"/> is safe for the render thread (no locks, no alloc).
/// </summary>
internal sealed class BoundedManagedPcmCapture
{
    private const int StateIdle = 0;
    private const int StateActive = 1;
    private const int StateComplete = 2;

    private readonly float[] _samples;
    private int _skipRemaining;
    private int _write;
    private int _state;

    public BoundedManagedPcmCapture(int skipSamples, int captureSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skipSamples);
        ArgumentOutOfRangeException.ThrowIfLessThan(captureSamples, 1);
        _samples = new float[captureSamples];
        _skipRemaining = skipSamples;
        Volatile.Write(ref _state, StateActive);
    }

    public bool IsComplete => Volatile.Read(ref _state) == StateComplete;

    public int CapturedCount => Volatile.Read(ref _write);

    public int Capacity => _samples.Length;

    /// <summary>Offers one period of interleaved floats (callback-safe).</summary>
    public void Offer(ReadOnlySpan<float> data)
    {
        if (Volatile.Read(ref _state) != StateActive || data.IsEmpty)
        {
            return;
        }

        var skip = _skipRemaining;
        if (skip > 0)
        {
            if (data.Length <= skip)
            {
                _skipRemaining = skip - data.Length;
                return;
            }

            data = data[skip..];
            _skipRemaining = 0;
        }

        var write = _write;
        var remaining = _samples.Length - write;
        if (remaining <= 0)
        {
            Volatile.Write(ref _state, StateComplete);
            return;
        }

        var n = Math.Min(remaining, data.Length);
        data[..n].CopyTo(_samples.AsSpan(write));
        write += n;
        Volatile.Write(ref _write, write);

        if (write >= _samples.Length)
        {
            Volatile.Write(ref _state, StateComplete);
        }
    }

    public bool TryCopyCaptured(Span<float> destination, out int written)
    {
        written = 0;
        if (Volatile.Read(ref _state) != StateComplete)
        {
            return false;
        }

        var count = Volatile.Read(ref _write);
        if (destination.Length < count)
        {
            return false;
        }

        _samples.AsSpan(0, count).CopyTo(destination);
        written = count;
        return true;
    }

    public float[] ToArray()
    {
        var count = Volatile.Read(ref _write);
        var copy = new float[count];
        _samples.AsSpan(0, count).CopyTo(copy);
        return copy;
    }
}
