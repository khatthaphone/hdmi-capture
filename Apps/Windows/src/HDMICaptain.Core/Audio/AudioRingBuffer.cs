namespace HDMICaptain.Core.Audio;

/// <summary>
/// Single-producer / single-consumer lock-free ring of interleaved float frames (port of CaptureCore's AudioRingBuffer).
/// Write and Read never allocate or block; the producer is the capture thread, the consumer the render thread.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly float[] _storage;
    private long _written;   // frames ever written (producer-owned)
    private long _consumed;  // frames ever read (consumer-owned)
    private long _overflow;
    private long _underflow;

    public AudioRingBuffer(int capacityFrames, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        CapacityFrames = capacityFrames;
        Channels = channels;
        _storage = new float[checked(capacityFrames * channels)];
    }

    public int Channels { get; }

    public int CapacityFrames { get; }

    /// <summary>Frames currently buffered. Safe from either thread (momentarily stale).</summary>
    public int FillFrames
    {
        get
        {
            // Both counters only grow; reading consumed first can only over-estimate by in-flight frames, never go negative.
            long r = Volatile.Read(ref _consumed);
            long w = Volatile.Read(ref _written);
            return (int)Math.Clamp(w - r, 0, CapacityFrames);
        }
    }

    public long OverflowFrames => Volatile.Read(ref _overflow);

    public long UnderflowFrames => Volatile.Read(ref _underflow);

    /// <summary>Writes whole frames; frames that do not fit are dropped and counted. Returns frames stored.</summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / Channels;
        long w = Volatile.Read(ref _written);
        long r = Volatile.Read(ref _consumed);
        int free = CapacityFrames - (int)(w - r);
        int count = Math.Min(frames, free);
        if (count < frames) Interlocked.Add(ref _overflow, frames - count);
        if (count <= 0) return 0;

        int start = (int)(w % CapacityFrames);
        int first = Math.Min(count, CapacityFrames - start);
        interleaved[..(first * Channels)].CopyTo(_storage.AsSpan(start * Channels));
        if (count > first) interleaved.Slice(first * Channels, (count - first) * Channels).CopyTo(_storage);
        Volatile.Write(ref _written, w + count);
        return count;
    }

    /// <summary>Reads up to Length/Channels frames. Returns frames read; the rest of the span is untouched.</summary>
    public int Read(Span<float> interleaved)
    {
        int frames = interleaved.Length / Channels;
        long r = Volatile.Read(ref _consumed);
        long w = Volatile.Read(ref _written);
        int count = (int)Math.Min(frames, w - r);
        if (count <= 0) return 0;

        int start = (int)(r % CapacityFrames);
        int first = Math.Min(count, CapacityFrames - start);
        _storage.AsSpan(start * Channels, first * Channels).CopyTo(interleaved);
        if (count > first) _storage.AsSpan(0, (count - first) * Channels).CopyTo(interleaved[(first * Channels)..]);
        Volatile.Write(ref _consumed, r + count);
        return count;
    }

    /// <summary>Consumer side: drops up to <paramref name="frames"/> buffered frames (to jump back to the target fill).</summary>
    public int Discard(int frames)
    {
        long r = Volatile.Read(ref _consumed);
        long w = Volatile.Read(ref _written);
        int count = (int)Math.Min(frames, w - r);
        if (count <= 0) return 0;
        Volatile.Write(ref _consumed, r + count);
        return count;
    }

    public void NoteUnderflow(int frames)
    {
        if (frames > 0) Interlocked.Add(ref _underflow, frames);
    }

    /// <summary>Empties the ring and resets the counters. Only when both sides are stopped.</summary>
    public void Clear()
    {
        Volatile.Write(ref _consumed, Volatile.Read(ref _written));
        Interlocked.Exchange(ref _overflow, 0);
        Interlocked.Exchange(ref _underflow, 0);
    }
}
