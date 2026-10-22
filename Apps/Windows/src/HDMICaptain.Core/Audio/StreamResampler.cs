namespace HDMICaptain.Core.Audio;

/// <summary>Supplies input frames: fills <paramref name="destination"/> (interleaved) and returns whole frames provided.</summary>
public delegate int PullFrames(Span<float> destination);

/// <summary>
/// Variable-ratio resampler (4-point Catmull-Rom/Hermite). Click-free for slowly changing ratios; intended for ratios near 1
/// or upsampling. Large-factor downsampling is not band-limited. Process never allocates (cache the pull delegate, or use the ring overload).
/// </summary>
public sealed class StreamResampler
{
    private const int BufferFrames = 4096;
    private const double MinRatio = 0.25, MaxRatio = 4;

    private readonly int _channels;
    private readonly float[] _buffer;
    private int _have;      // valid frames in _buffer
    private double _pos;    // read position (frames) within _buffer

    public StreamResampler(int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _channels = channels;
        _buffer = new float[BufferFrames * channels];
        Reset();
    }

    /// <summary>
    /// Writes exactly output.Length/channels frames. ratio = inputRate/outputRate * correction. <paramref name="pull"/> is asked for
    /// only the frames needed and may return fewer (underflow): the shortfall is filled with silence.
    /// </summary>
    public void Process(Span<float> output, double ratio, PullFrames pull) =>
        Run(output, ratio, new DelegateSource(pull));

    /// <summary>Same as the delegate overload but reads straight from a ring (no delegate); underflow is noted on the ring.</summary>
    public void Process(Span<float> output, double ratio, AudioRingBuffer source) =>
        Run(output, ratio, new RingSource(source));

    public void Reset()
    {
        Array.Clear(_buffer);
        _have = 1;   // one zero frame of history so the first output equals the first input at ratio 1
        _pos = 1;
    }

    private interface IFrameSource
    {
        int Pull(Span<float> destination);
        void Short(int frames);
    }

    private readonly struct DelegateSource(PullFrames pull) : IFrameSource
    {
        public int Pull(Span<float> destination) => pull(destination);
        public void Short(int frames) { }
    }

    private readonly struct RingSource(AudioRingBuffer ring) : IFrameSource
    {
        public int Pull(Span<float> destination) => ring.Read(destination);
        public void Short(int frames) => ring.NoteUnderflow(frames);
    }

    private void Run<T>(Span<float> output, double ratio, T source) where T : IFrameSource
    {
        int ch = _channels;
        int frames = output.Length / ch;
        ratio = Math.Clamp(ratio, MinRatio, MaxRatio);
        float[] buf = _buffer;
        for (int o = 0; o < frames; o++)
        {
            int idx = (int)_pos;
            if (idx + 2 >= _have)
            {
                Refill(idx, frames - o, ratio, source);
                idx = (int)_pos;
            }
            float t = (float)(_pos - idx);
            int b = idx * ch;
            for (int c = 0; c < ch; c++)
            {
                float ym1 = buf[b - ch + c], y0 = buf[b + c], y1 = buf[b + ch + c], y2 = buf[b + 2 * ch + c];
                float c1 = 0.5f * (y1 - ym1);
                float c2 = ym1 - 2.5f * y0 + 2f * y1 - 0.5f * y2;
                float c3 = 0.5f * (y2 - ym1) + 1.5f * (y0 - y1);
                output[o * ch + c] = ((c3 * t + c2) * t + c1) * t + y0;
            }
            _pos += ratio;
        }
    }

    private void Refill<T>(int idx, int remainingOutputs, double ratio, T source) where T : IFrameSource
    {
        int ch = _channels;
        int drop = Math.Max(0, idx - 1);
        if (drop > 0)
        {
            _buffer.AsSpan(drop * ch, (_have - drop) * ch).CopyTo(_buffer);
            _have -= drop;
            _pos -= drop;
        }
        int needed = (int)Math.Ceiling(_pos + remainingOutputs * ratio) + 3 - _have;
        int want = Math.Clamp(needed, 1, BufferFrames - _have);
        Span<float> dest = _buffer.AsSpan(_have * ch, want * ch);
        int got = Math.Clamp(source.Pull(dest), 0, want);
        if (got < want)
        {
            dest[(got * ch)..].Clear();
            source.Short(want - got);
        }
        _have += want;
    }
}
