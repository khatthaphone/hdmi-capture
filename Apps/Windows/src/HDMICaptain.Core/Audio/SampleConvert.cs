using System.Buffers.Binary;

namespace HDMICaptain.Core.Audio;

/// <summary>Sample format conversion for WASAPI shared-mode buffers (little endian). No allocations.</summary>
public static class SampleConvert
{
    /// <summary>
    /// Interleaved capture bytes (16/24-packed/32-bit PCM, where 24-in-32 is just 32-bit, or 32-bit float; 1 or more channels)
    /// to interleaved stereo float. Takes the first two channels, mono is duplicated. Returns frames written (limited by output size).
    /// </summary>
    public static int ToStereoFloat(ReadOnlySpan<byte> input, int channels, int bitsPerSample, bool isFloat, Span<float> output)
    {
        int bytes = bitsPerSample / 8;
        int frames = Math.Min(input.Length / (bytes * channels), output.Length / 2);
        for (int f = 0; f < frames; f++)
        {
            int at = f * channels * bytes;
            float l = Read(input, at, bitsPerSample, isFloat);
            float r = channels > 1 ? Read(input, at + bytes, bitsPerSample, isFloat) : l;
            output[2 * f] = l;
            output[2 * f + 1] = r;
        }
        return frames;
    }

    /// <summary>
    /// Stereo float (scaled by <paramref name="volume"/>) into a mix-format buffer with <paramref name="channels"/> channels:
    /// stereo goes to the first two, the rest are silent (mono gets the average). Writes stereo.Length/2 frames.
    /// </summary>
    public static void FromStereoFloat(ReadOnlySpan<float> stereo, Span<byte> output, int channels, int bitsPerSample, bool isFloat, float volume)
    {
        int bytes = bitsPerSample / 8;
        int frames = Math.Min(stereo.Length / 2, output.Length / (bytes * channels));
        output[..(frames * bytes * channels)].Clear();
        for (int f = 0; f < frames; f++)
        {
            float l = stereo[2 * f] * volume, r = stereo[2 * f + 1] * volume;
            int at = f * channels * bytes;
            if (channels == 1)
            {
                Write(output, at, bitsPerSample, isFloat, (l + r) * 0.5f);
            }
            else
            {
                Write(output, at, bitsPerSample, isFloat, l);
                Write(output, at + bytes, bitsPerSample, isFloat, r);
            }
        }
    }

    private static float Read(ReadOnlySpan<byte> b, int at, int bits, bool isFloat)
    {
        if (isFloat) return BinaryPrimitives.ReadSingleLittleEndian(b[at..]);
        return bits switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(b[at..]) / 32768f,
            24 => (((b[at + 2] << 24) | (b[at + 1] << 16) | (b[at] << 8)) >> 8) / 8388608f,
            _ => BinaryPrimitives.ReadInt32LittleEndian(b[at..]) / 2147483648f,
        };
    }

    private static void Write(Span<byte> b, int at, int bits, bool isFloat, float v)
    {
        if (isFloat)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b[at..], v);
            return;
        }
        v = Math.Clamp(v, -1f, 1f);
        switch (bits)
        {
            case 16:
                BinaryPrimitives.WriteInt16LittleEndian(b[at..], (short)Math.Clamp((int)MathF.Round(v * 32768f), short.MinValue, short.MaxValue));
                break;
            case 24:
                int s24 = Math.Clamp((int)MathF.Round(v * 8388608f), -8388608, 8388607);
                b[at] = (byte)s24;
                b[at + 1] = (byte)(s24 >> 8);
                b[at + 2] = (byte)(s24 >> 16);
                break;
            default:
                BinaryPrimitives.WriteInt32LittleEndian(b[at..], (int)Math.Clamp(Math.Round(v * 2147483648.0), int.MinValue, int.MaxValue));
                break;
        }
    }
}
