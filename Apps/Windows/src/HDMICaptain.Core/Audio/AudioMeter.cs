namespace HDMICaptain.Core.Audio;

public static class AudioMeter
{
    /// <summary>Peak and RMS per channel of an interleaved stereo block. No allocations.</summary>
    public static (float PeakL, float PeakR, float RmsL, float RmsR) Measure(ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        if (frames == 0) return default;
        float peakL = 0, peakR = 0;
        double sumL = 0, sumR = 0;
        for (int i = 0; i < frames; i++)
        {
            float l = stereo[2 * i], r = stereo[2 * i + 1];
            peakL = Math.Max(peakL, Math.Abs(l));
            peakR = Math.Max(peakR, Math.Abs(r));
            sumL += (double)l * l;
            sumR += (double)r * r;
        }
        return (peakL, peakR, (float)Math.Sqrt(sumL / frames), (float)Math.Sqrt(sumR / frames));
    }
}
