using HDMICaptain.Core.Audio;

namespace HDMICaptain.Core.Tests;

public class AudioRingBufferTests
{
    private static float[] Frames(int start, int count)
    {
        var data = new float[count * 2];
        for (int i = 0; i < count; i++)
        {
            data[2 * i] = start + i;
            data[2 * i + 1] = -(start + i);
        }
        return data;
    }

    [Fact]
    public void WrapsAround()
    {
        var ring = new AudioRingBuffer(8);
        var next = 0;
        var expect = 0;
        for (int round = 0; round < 20; round++)
        {
            Assert.Equal(5, ring.Write(Frames(next, 5)));
            next += 5;
            var got = new float[5 * 2];
            Assert.Equal(5, ring.Read(got));
            for (int i = 0; i < 5; i++, expect++)
            {
                Assert.Equal(expect, got[2 * i]);
                Assert.Equal(-expect, got[2 * i + 1]);
            }
        }
        Assert.Equal(0, ring.FillFrames);
        Assert.Equal(0, ring.OverflowFrames);
    }

    [Fact]
    public void DropsExcessAndCountsOverflow()
    {
        var ring = new AudioRingBuffer(8);
        Assert.Equal(8, ring.Write(Frames(0, 12)));
        Assert.Equal(8, ring.FillFrames);
        Assert.Equal(4, ring.OverflowFrames);
        Assert.Equal(0, ring.Write(Frames(100, 3)));
        Assert.Equal(7, ring.OverflowFrames);
        var got = new float[16];
        Assert.Equal(8, ring.Read(got));
        Assert.Equal(7, got[14]);
    }

    [Fact]
    public void ReadReturnsOnlyWhatIsThere()
    {
        var ring = new AudioRingBuffer(8);
        ring.Write(Frames(0, 3));
        var got = new float[10 * 2];
        Assert.Equal(3, ring.Read(got));
        Assert.Equal(0, ring.Read(got));
        ring.NoteUnderflow(7);
        Assert.Equal(7, ring.UnderflowFrames);
        ring.Clear();
        Assert.Equal(0, ring.UnderflowFrames);
    }

    [Fact]
    public void ClearEmptiesTheRing()
    {
        var ring = new AudioRingBuffer(8);
        ring.Write(Frames(0, 6));
        ring.Clear();
        Assert.Equal(0, ring.FillFrames);
        Assert.Equal(8, ring.Write(Frames(0, 8)));
    }

    [Fact]
    public void ConcurrentProducerConsumerKeepsOrder()
    {
        const int total = 1_000_000;
        var ring = new AudioRingBuffer(1024);
        var producer = new Thread(() =>
        {
            int sent = 0;
            while (sent < total)
            {
                int n = Math.Min(37, total - sent);
                if (ring.CapacityFrames - ring.FillFrames < n) { Thread.Yield(); continue; }
                ring.Write(Frames(sent, n));
                sent += n;
            }
        });
        producer.Start();

        var buf = new float[64 * 2];
        int received = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (received < total && DateTime.UtcNow < deadline)
        {
            int n = ring.Read(buf);
            if (n == 0) { Thread.Yield(); continue; }
            for (int i = 0; i < n; i++, received++)
            {
                Assert.Equal(received, buf[2 * i]);
                Assert.Equal(-received, buf[2 * i + 1]);
            }
        }
        producer.Join();
        Assert.Equal(total, received);
        Assert.Equal(0, ring.OverflowFrames);
    }
}

public class DriftControllerTests
{
    [Fact]
    public void MinimumTargetCoversAPacketAndAPeriod()
    {
        // UGREEN-25854: 480-frame capture packets and render periods at 48 kHz.
        Assert.Equal(23, DriftController.MinimumTargetMs(10, 10), 6);
        // Small packets and periods: the 20 ms base wins in the pipeline.
        Assert.True(DriftController.MinimumTargetMs(1, 3) < 20);
    }

    [Fact]
    public void PhaseFreeFillIgnoresWhereTheReadFallsBetweenPackets()
    {
        // 480-frame packets every 10 ms; the ring holds 576 frames as a packet lands and drains at 48 frames/ms.
        // A read 0.1 ms after the packet and one 9.9 ms after it (just before the next) see the same latency.
        double after = DriftController.PhaseFreeFillFrames(576 - 5, 0.1, 10, 48_000);
        double before = DriftController.PhaseFreeFillFrames(576 - 475, 9.9, 10, 48_000);
        Assert.InRange(Math.Abs(before - after), 0, 2);
        // A capture stall adds at most one packet.
        Assert.Equal(100 + 480, DriftController.PhaseFreeFillFrames(100, 250, 10, 48_000), 6);
    }

    private const double Rate = 48000, Period = 0.01;

    /// <summary>Capture runs ppmOffset fast relative to render; returns the fill (ms) after every render period.</summary>
    private static IEnumerable<(double Seconds, double FillMs)> Simulate(DriftController drift, double ppmOffset, double seconds, Func<double, double>? retarget = null)
    {
        double fill = drift.TargetMs * Rate / 1000;
        long steps = (long)(seconds / Period);
        for (long i = 0; i < steps; i++)
        {
            double t = i * Period;
            if (retarget != null) drift.TargetMs = retarget(t);
            double correction = drift.Update((int)Math.Round(fill), Period);
            fill += Rate * (1 + ppmOffset * 1e-6) * Period - Rate * Period * correction;
            yield return (t, fill * 1000 / Rate);
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(-100)]
    public void HoldsFillWithinTenMsOverAnHour(double ppm)
    {
        var drift = new DriftController(Rate, 60);
        double worst = 0;
        foreach (var (t, fillMs) in Simulate(drift, ppm, 3600))
            if (t > 300) worst = Math.Max(worst, Math.Abs(fillMs - 60));
        Assert.True(worst < 10, $"worst deviation {worst:F2} ms");
    }

    [Fact]
    public void CorrectionStaysWithinClamp()
    {
        var drift = new DriftController(Rate, 20);
        double min = 1, max = 1;
        double fill = 5000;   // far above target
        for (int i = 0; i < 1000; i++)
        {
            double c = drift.Update((int)fill, Period);
            min = Math.Min(min, c);
            max = Math.Max(max, c);
        }
        Assert.InRange(min, 1 - DriftController.MaxPpm * 1e-6, 1 + DriftController.MaxPpm * 1e-6);
        Assert.InRange(max, 1, 1 + DriftController.MaxPpm * 1e-6 + 1e-12);
        Assert.True(max > 1.0015);
    }

    [Fact]
    public void RetargetsWhenTargetChanges()
    {
        var drift = new DriftController(Rate, 40);
        double last = 0;
        foreach (var (t, fillMs) in Simulate(drift, 50, 1800, t => t < 60 ? 40 : 140))
            if (t > 1500) last = Math.Max(last, Math.Abs(fillMs - 140));
        Assert.True(last < 10, $"deviation after retarget {last:F2} ms");
        Assert.Equal(140, drift.CurrentTargetMs, 0.001);
    }

    [Fact]
    public void RetargetIsSlewedNotJumped()
    {
        var drift = new DriftController(Rate, 40);
        drift.Update(1920, Period);
        drift.TargetMs = 440;
        drift.Update(1920, Period);
        Assert.True(drift.CurrentTargetMs < 45);
    }
}

public class StreamResamplerTests
{
    private static PullFrames Sine(double freq, double rate, double amp, long startFrame = 0)
    {
        long n = startFrame;
        return dest =>
        {
            int frames = dest.Length / 2;
            for (int i = 0; i < frames; i++, n++)
            {
                float v = (float)(amp * Math.Sin(2 * Math.PI * freq * n / rate));
                dest[2 * i] = v;
                dest[2 * i + 1] = -v;
            }
            return frames;
        };
    }

    private static float[] Run(StreamResampler rs, PullFrames pull, double ratio, int frames, int block = 480)
    {
        var all = new float[frames * 2];
        for (int done = 0; done < frames; done += block)
        {
            int n = Math.Min(block, frames - done);
            rs.Process(all.AsSpan(done * 2, n * 2), ratio, pull);
        }
        return all;
    }

    [Fact]
    public void RatioOneIsIdentity()
    {
        long n = 0;
        PullFrames pull = dest =>
        {
            int frames = dest.Length / 2;
            for (int i = 0; i < frames; i++, n++)
            {
                dest[2 * i] = MathF.Sin(n * 0.37f) * 0.8f;
                dest[2 * i + 1] = MathF.Cos(n * 0.11f) * 0.5f;
            }
            return frames;
        };
        var output = Run(new StreamResampler(), pull, 1.0, 4800, 333);
        for (int i = 0; i < 4800; i++)
        {
            Assert.Equal(MathF.Sin(i * 0.37f) * 0.8f, output[2 * i], 1e-5);
            Assert.Equal(MathF.Cos(i * 0.11f) * 0.5f, output[2 * i + 1], 1e-5);
        }
    }

    private static int ZeroCrossings(float[] interleaved, int channel)
    {
        int count = 0;
        for (int i = 1; i < interleaved.Length / 2; i++)
            if ((interleaved[2 * (i - 1) + channel] < 0) != (interleaved[2 * i + channel] < 0)) count++;
        return count;
    }

    private static double Rms(float[] interleaved, int skipFrames)
    {
        double sum = 0;
        int n = interleaved.Length / 2 - skipFrames;
        for (int i = skipFrames; i < interleaved.Length / 2; i++) sum += interleaved[2 * i] * interleaved[2 * i];
        return Math.Sqrt(sum / n);
    }

    [Theory]
    [InlineData(48000.0, 44100.0)]
    [InlineData(44100.0, 48000.0)]
    public void ConversionKeepsFrequencyAndAmplitude(double inRate, double outRate)
    {
        int outFrames = (int)outRate;   // one second
        var output = Run(new StreamResampler(), Sine(1000, inRate, 0.5, 0), inRate / outRate, outFrames);
        // 1 kHz for one second: 2000 crossings.
        Assert.InRange(ZeroCrossings(output, 0), 1998, 2002);
        Assert.Equal(0.5 / Math.Sqrt(2), Rms(output, 100), 0.5 / Math.Sqrt(2) * 0.01);
        Assert.InRange(output.Max(), 0.49, 0.5001);
    }

    [Fact]
    public void RatioStepsOfOneHundredPpmDoNotClick()
    {
        double freq = 440, rate = 48000;
        var rs = new StreamResampler();
        var pull = Sine(freq, rate, 0.5);
        double maxSlope = 2 * Math.PI * freq / rate * 0.5 * 1.02;
        var block = new float[480 * 2];
        float prev = 0;
        bool first = true;
        double ratio = 1;
        for (int b = 0; b < 400; b++)
        {
            ratio = 1 + 100e-6 * (b % 2 == 0 ? 1 : -1) * (b / 50 + 1);   // steps of 100 ppm and more every block
            rs.Process(block, ratio, pull);
            for (int i = 0; i < 480; i++)
            {
                float v = block[2 * i];
                if (!first) Assert.True(Math.Abs(v - prev) <= maxSlope * 1.1, $"jump {Math.Abs(v - prev)} at block {b}");
                prev = v;
                first = false;
            }
        }
    }

    [Fact]
    public void UnderflowProducesSilenceAndNotesIt()
    {
        var rs = new StreamResampler();
        var output = new float[480 * 2];
        rs.Process(output, 1.0, _ => 0);
        Assert.All(output, v => Assert.Equal(0f, v));

        var ring = new AudioRingBuffer(1024);
        rs.Reset();
        rs.Process(output, 1.0, ring);
        Assert.All(output, v => Assert.Equal(0f, v));
        Assert.True(ring.UnderflowFrames > 0);
    }

    [Fact]
    public void RingOverloadMatchesDelegate()
    {
        var ring = new AudioRingBuffer(8192);
        var source = new float[2000 * 2];
        for (int i = 0; i < 2000; i++) { source[2 * i] = MathF.Sin(i * 0.05f); source[2 * i + 1] = MathF.Cos(i * 0.05f); }
        ring.Write(source);
        var a = new float[1000 * 2];
        new StreamResampler().Process(a, 0.9999, ring);

        var ring2 = new AudioRingBuffer(8192);
        ring2.Write(source);
        var b = new float[1000 * 2];
        new StreamResampler().Process(b, 0.9999, ring2.Read);
        Assert.Equal(a, b);
    }
}

public class ChannelMapperTests
{
    private static float[] Apply(ChannelMode mode)
    {
        float[] data = [1f, 0.5f, -1f, 0.25f];
        ChannelMapper.Apply(mode, data);
        return data;
    }

    [Fact] public void AutoAndNormalAreUntouched()
    {
        Assert.Equal([1f, 0.5f, -1f, 0.25f], Apply(ChannelMode.Auto));
        Assert.Equal([1f, 0.5f, -1f, 0.25f], Apply(ChannelMode.Normal));
    }

    [Fact] public void Swap() => Assert.Equal([0.5f, 1f, 0.25f, -1f], Apply(ChannelMode.Swap));

    [Fact] public void LeftToBoth() => Assert.Equal([1f, 1f, -1f, -1f], Apply(ChannelMode.LeftToBoth));

    [Fact] public void RightToBoth() => Assert.Equal([0.5f, 0.5f, 0.25f, 0.25f], Apply(ChannelMode.RightToBoth));

    [Fact] public void MonoAverages() => Assert.Equal([0.75f, 0.75f, -0.375f, -0.375f], Apply(ChannelMode.Mono));
}

public class AudioMeterTests
{
    [Fact]
    public void PeakAndRmsPerChannel()
    {
        float[] block = [0.5f, 0f, -1f, 0f, 0.5f, 0f, -1f, 0f];
        var (pl, pr, rl, rr) = AudioMeter.Measure(block);
        Assert.Equal(1f, pl);
        Assert.Equal(0f, pr);
        Assert.Equal(MathF.Sqrt((0.25f + 1f) / 2), rl, 1e-6);
        Assert.Equal(0f, rr);
    }

    [Fact]
    public void EmptyBlockIsSilent() => Assert.Equal((0f, 0f, 0f, 0f), AudioMeter.Measure([]));
}

public class SampleConvertTests
{
    private static readonly float[] Stereo = [0f, 0.5f, -0.5f, 0.25f, 0.999f, -1f];

    [Theory]
    [InlineData(16, false, 2)]
    [InlineData(24, false, 3)]
    [InlineData(32, false, 4)]
    [InlineData(32, true, 4)]
    public void StereoRoundTrip(int bits, bool isFloat, int bytes)
    {
        var buffer = new byte[3 * 2 * bytes];
        SampleConvert.FromStereoFloat(Stereo, buffer, 2, bits, isFloat, 1f);
        var back = new float[6];
        Assert.Equal(3, SampleConvert.ToStereoFloat(buffer, 2, bits, isFloat, back));
        double tolerance = bits == 16 ? 1.0 / 32768 : 1e-6;
        for (int i = 0; i < 6; i++) Assert.Equal(Stereo[i], back[i], tolerance);
    }

    [Fact]
    public void TwentyFourInThirtyTwoIsLeftJustified()
    {
        var buffer = new byte[4];
        BitConverter.GetBytes(0x40000000).CopyTo(buffer, 0);   // 0.5, low byte zero as WASAPI delivers
        var output = new float[2];
        Assert.Equal(1, SampleConvert.ToStereoFloat(buffer, 1, 32, false, output));
        Assert.Equal(0.5f, output[0]);
        Assert.Equal(0.5f, output[1]);
    }

    [Fact]
    public void MonoIsDuplicatedAndExtraChannelsIgnored()
    {
        var mono = new byte[2 * 2];
        BitConverter.GetBytes((short)16384).CopyTo(mono, 0);
        BitConverter.GetBytes((short)-8192).CopyTo(mono, 2);
        var output = new float[4];
        Assert.Equal(2, SampleConvert.ToStereoFloat(mono, 1, 16, false, output));
        Assert.Equal([0.5f, 0.5f, -0.25f, -0.25f], output);

        var six = new byte[6 * 4];
        BitConverter.GetBytes(0.25f).CopyTo(six, 0);
        BitConverter.GetBytes(-0.25f).CopyTo(six, 4);
        BitConverter.GetBytes(0.9f).CopyTo(six, 8);
        var two = new float[2];
        Assert.Equal(1, SampleConvert.ToStereoFloat(six, 6, 32, true, two));
        Assert.Equal([0.25f, -0.25f], two);
    }

    [Fact]
    public void RenderAppliesVolumeFillsSilentChannelsAndClips()
    {
        float[] stereo = [1f, -1f, 0.5f, 0.5f];
        var buffer = new byte[2 * 4 * 4];
        SampleConvert.FromStereoFloat(stereo, buffer, 4, 32, true, 0.5f);
        var floats = new float[8];
        Buffer.BlockCopy(buffer, 0, floats, 0, buffer.Length);
        Assert.Equal([0.5f, -0.5f, 0f, 0f, 0.25f, 0.25f, 0f, 0f], floats);

        var pcm = new byte[2 * 2];
        SampleConvert.FromStereoFloat([2f, -2f], pcm, 2, 16, false, 1f);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(pcm, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(pcm, 2));
    }
}
