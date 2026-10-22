using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HDMICaptain.Media.Capture;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Recording;

/// <summary>
/// AAC-LC (inbox sync MFT, 192 kbps stereo) on its own thread. Input is 16-bit PCM (the only input the
/// Microsoft AAC encoder takes); the capture thread converts and queues pooled chunks, so it never blocks.
/// </summary>
internal sealed unsafe class AudioEncoder
{
    public const int Channels = 2;
    private const int BytesPerSecond = 24_000; // 192 kbps

    private readonly int _rate;
    private readonly EncodedHandler _output;
    private readonly Action<Exception> _failed;
    private readonly BlockingCollection<Chunk?> _input = new(new ConcurrentQueue<Chunk?>());
    private readonly Thread _thread;
    private IMFActivate? _activate;
    private MftDriver? _driver;
    private long _lastOutputEnd;

    private sealed record Chunk(short[] Pcm, int Frames, long Time);

    public AudioEncoder(int rate, EncodedHandler output, Action<Exception> failed)
    {
        _rate = rate;
        _output = output;
        _failed = failed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio encoder" };
    }

    /// <summary>The AAC output type (with its AudioSpecificConfig in MF_MT_USER_DATA) for a passthrough writer.</summary>
    public IMFMediaType? OutputType { get; private set; }

    public ManualResetEventSlim Ready { get; } = new(false);

    public void Start() => _thread.Start();

    /// <summary>Converts interleaved stereo float to PCM16 and queues it. On the capture thread; no allocation in steady state.</summary>
    public void Enqueue(ReadOnlySpan<float> stereo, long time)
    {
        var frames = stereo.Length / Channels;
        if (frames == 0) return;
        var pcm = ArrayPool<short>.Shared.Rent(frames * Channels);
        for (var i = 0; i < frames * Channels; i++)
            pcm[i] = (short)Math.Clamp(MathF.Round(stereo[i] * 32767f), short.MinValue, short.MaxValue);
        try
        {
            if (_input.TryAdd(new Chunk(pcm, frames, time))) return;
        }
        catch (InvalidOperationException)
        {
            // completed
        }
        ArrayPool<short>.Shared.Return(pcm);
    }

    public void Complete()
    {
        try
        {
            _input.Add(null);
            _input.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Join() => _thread.Join();

    private void Run()
    {
        try
        {
            Initialize();
            Ready.Set();
            foreach (var chunk in _input.GetConsumingEnumerable())
            {
                if (chunk == null) break;
                Encode(chunk);
            }
            _driver!.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM);
            _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN);
            PullAll();
        }
        catch (Exception e)
        {
            Ready.Set();
            _failed(e);
            foreach (var chunk in _input.GetConsumingEnumerable())
                if (chunk != null) ArrayPool<short>.Shared.Return(chunk.Pcm);
        }
        finally
        {
            if (_driver != null) MfInterop.Release(_driver.Transform);
            if (_activate != null)
            {
                try
                {
                    _activate.ShutdownObject();
                }
                catch (COMException)
                {
                }
                MfInterop.Release(_activate);
            }
        }
    }

    private void Initialize()
    {
        var found = MfMedia.Enumerate(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT,
            PInvoke.MFMediaType_Audio, PInvoke.MFAudioFormat_PCM, PInvoke.MFAudioFormat_AAC);
        if (found.Count == 0) throw new InvalidOperationException("No AAC encoder on this PC.");
        _activate = found[0].Activate;
        for (var i = 1; i < found.Count; i++) MfInterop.Release(found[i].Activate);

        var transform = MfMedia.ActivateTransform(_activate);
        _driver = new MftDriver(transform);

        var input = MfMedia.CreateMediaType();
        var output = MfMedia.CreateMediaType();
        try
        {
            input.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            input.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_PCM);
            input.SetUInt32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            input.SetUInt32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
            input.SetUInt32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            input.SetUInt32(PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, Channels * 2);
            input.SetUInt32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(_rate * Channels * 2));

            output.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            output.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_AAC);
            output.SetUInt32(PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            output.SetUInt32(PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
            output.SetUInt32(PInvoke.MF_MT_AUDIO_NUM_CHANNELS, Channels);
            output.SetUInt32(PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, BytesPerSecond);
            output.SetUInt32(PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0); // raw AAC; the MP4 sink adds the esds box

            transform.SetInputType(0, input, 0);
            transform.SetOutputType(0, output, 0);
        }
        finally
        {
            MfInterop.Release(input);
            MfInterop.Release(output);
        }

        transform.GetOutputCurrentType(0, out var current);
        OutputType = MfMedia.Clone(current);
        MfInterop.Release(current);
        _driver.ReadStreamInfo();
        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING);
        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM);
    }

    private void Encode(Chunk chunk)
    {
        IMFSample sample;
        try
        {
            var bytes = MemoryMarshal.AsBytes(chunk.Pcm.AsSpan(0, chunk.Frames * Channels));
            sample = MfMedia.CreateSample(bytes, chunk.Time, chunk.Frames * 10_000_000L / _rate);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(chunk.Pcm);
        }
        try
        {
            while (true)
            {
                try
                {
                    _driver!.Transform.ProcessInput(0, sample, 0);
                    break;
                }
                catch (COMException e) when (e.HResult == MfMedia.NotAccepting)
                {
                    PullAll();
                }
            }
        }
        finally
        {
            MfInterop.Release(sample);
        }
        PullAll();
    }

    private void PullAll()
    {
        while (_driver!.Pull() is { } sample)
        {
            var duration = MfMedia.DurationOrNull(sample) is { } d && d > 0 ? d : 1024 * 10_000_000L / _rate;
            var time = MfMedia.TimeOrNull(sample) ?? _lastOutputEnd;
            _lastOutputEnd = time + duration;
            _output(sample, time, duration, true);
        }
    }
}
