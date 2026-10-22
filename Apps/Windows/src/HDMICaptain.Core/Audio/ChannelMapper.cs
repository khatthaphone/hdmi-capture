namespace HDMICaptain.Core.Audio;

/// <summary>Channel routing for the captured stereo pair (CaptureCore's ChannelMode). No allocations.</summary>
public static class ChannelMapper
{
    /// <summary>In place on interleaved stereo. Auto behaves like Normal (auto detection belongs to the pipeline).</summary>
    public static void Apply(ChannelMode mode, Span<float> stereo)
    {
        int n = stereo.Length & ~1;
        switch (mode)
        {
            case ChannelMode.Swap:
                for (int i = 0; i < n; i += 2) (stereo[i], stereo[i + 1]) = (stereo[i + 1], stereo[i]);
                break;
            case ChannelMode.LeftToBoth:
                for (int i = 0; i < n; i += 2) stereo[i + 1] = stereo[i];
                break;
            case ChannelMode.RightToBoth:
                for (int i = 0; i < n; i += 2) stereo[i] = stereo[i + 1];
                break;
            case ChannelMode.Mono:
                for (int i = 0; i < n; i += 2) stereo[i] = stereo[i + 1] = (stereo[i] + stereo[i + 1]) * 0.5f;
                break;
        }
    }
}
