using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace HDMICaptain.Media.Capture;

/// <summary>A capture failure: the HRESULT and where it surfaced (open, set type, ReadSample, OnEvent).</summary>
public sealed record CaptureError(int HResult, string Stage)
{
    public string Name => MfErrors.Name(HResult);

    public override string ToString() => $"{Stage}: {Name}";
}

/// <summary>Frames and gaps counted from sample timestamps (task 089 step 5).</summary>
public sealed class CaptureStats
{
    private long _frames;
    private long _dropped;

    public long Frames => Interlocked.Read(ref _frames);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long LastTimestamp { get; private set; } = -1;

    internal void Count(long timestamp, double expectedPeriod)
    {
        Interlocked.Increment(ref _frames);
        if (LastTimestamp >= 0 && expectedPeriod > 0)
        {
            // A gap of 1.5 periods or more means the device or the reader skipped frames.
            var missed = (long)Math.Round((timestamp - LastTimestamp) / expectedPeriod) - 1;
            if (missed > 0) Interlocked.Add(ref _dropped, missed);
        }
        LastTimestamp = timestamp;
    }
}

/// <summary>
/// Live capture from one card through an asynchronous <c>IMFSourceReader</c> (spec §13.2, windows.md §2b):
/// MF_LOW_LATENCY, no converters, the native type set directly, one ReadSample in flight. Frames are handed
/// to <see cref="FrameArrived"/> on MF's work-queue thread while the buffer is locked; no copy, no UI hop.
/// </summary>
public sealed class MfSource : ICaptureSession
{
    private const uint VideoStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    private const uint AllStreams = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ALL_STREAMS);

    private readonly string _symbolicLink;
    private readonly FormatPreference? _preference;
    private readonly UsbLink? _link;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _flushed = new(false);
    private IMFMediaSource? _source;
    private IMFSourceReader? _reader;
    private ReaderCallback? _callback;
    private NativeVideoType? _type;
    private double _period;
    private volatile bool _running;
    private bool _warnedShortBuffer;
    private readonly MjpegPolicy _mjpegPolicy;
    private MjpegDecoder? _decoder;
    private string? _lastDecodeError;
    private int _decodeCount;
    private double _decodeSum, _decodeMax;

    public MfSource(string symbolicLink, FormatPreference? preference = null, UsbLink? link = null,
        MjpegPolicy mjpegPolicy = MjpegPolicy.Auto)
    {
        _symbolicLink = symbolicLink;
        _preference = preference;
        _link = link;
        _mjpegPolicy = mjpegPolicy;
    }

    public event FrameHandler? FrameArrived;

    /// <summary>Each raw MJPEG frame before it is decoded, while the sample is locked (benchmarks, passthrough). MF thread.</summary>
    public event CompressedFrameHandler? CompressedFrameArrived;

    /// <summary>MJPG frames that failed to decode and were dropped.</summary>
    public long DecodeFailures { get; private set; }

    /// <summary>Raised once when capture stops on its own (unplug, preemption, privacy). On an MF thread.</summary>
    public event Action<CaptureError>? Failed;

    /// <summary>Diagnostic lines: chosen type, OnEvent types and statuses. On an MF thread.</summary>
    public event Action<string>? Log;

    public string SymbolicLink => _symbolicLink;
    public NativeVideoType? CurrentType => _type;
    public IReadOnlyList<NativeVideoType> NativeTypes { get; private set; } = [];
    public CaptureStats Stats { get; private set; } = new();

    /// <summary>Opens the device and starts reading. Throws <see cref="COMException"/> when the open fails (in use, privacy).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            MediaFoundation.Startup();
            try
            {
                Open();
            }
            catch
            {
                Close();
                MediaFoundation.Shutdown();
                throw;
            }
            _running = true;
            Request();
        }
    }

    private void Open()
    {
        _source = MfDevices.OpenSource(_symbolicLink);
        _callback = new ReaderCallback(this);
        var attributes = MfInterop.CreateAttributes(3);
        try
        {
            attributes.SetUnknown(PInvoke.MF_SOURCE_READER_ASYNC_CALLBACK, _callback);
            attributes.SetUInt32(PInvoke.MF_LOW_LATENCY, 1);
            attributes.SetUInt32(PInvoke.MF_READWRITE_DISABLE_CONVERTERS, 1);
            PInvoke.MFCreateSourceReaderFromMediaSource(_source, attributes, out var reader).ThrowOnFailure();
            _reader = reader;
        }
        finally
        {
            MfInterop.Release(attributes);
        }

        NativeTypes = MfDevices.ReadNativeTypes(_reader);
        _type = MfDevices.Choose(NativeTypes, _preference, _link, _mjpegPolicy)
            ?? throw new COMException("No native type we can show (MJPEG needs turbojpeg.dll).", MfErrors.NoMoreTypes);

        _reader.GetNativeMediaType(VideoStream, (uint)_type.Index, out var mediaType);
        try
        {
            unsafe { _reader.SetCurrentMediaType(VideoStream, null, mediaType); }
        }
        finally
        {
            MfInterop.Release(mediaType);
        }
        _reader.SetStreamSelection(AllStreams, false);
        _reader.SetStreamSelection(VideoStream, true);
        // Another app may already stream the card (Windows shares it). Trust what the reader runs, not what we asked.
        _reader.GetCurrentMediaType(VideoStream, out var current);
        try
        {
            var running = MfDevices.Describe(_type.Index, current);
            if (running.Subtype != _type.Subtype || running.Width != _type.Width || running.Height != _type.Height
                || running.RateNumerator * _type.RateDenominator != _type.RateNumerator * running.RateDenominator)
            {
                Log?.Invoke($"asked for {_type.Width}x{_type.Height} {_type.FourCC} @ {_type.FrameRate:F3}, " +
                            $"reader runs {running.Width}x{running.Height} {running.FourCC} @ {running.FrameRate:F3}");
                _type = running with { DecodedByWindows = _type.DecodedByWindows };
            }
        }
        finally
        {
            MfInterop.Release(current);
        }
        _period = _type.FrameRate > 0 ? 10_000_000 / _type.FrameRate : 0;
        Stats = new CaptureStats();
        DecodeFailures = 0;
        if (_type.FourCC == "MJPG") _decoder = new MjpegDecoder();
        Log?.Invoke($"type #{_type.Index}: {_type.Width}x{_type.Height} {_type.FourCC} @ {_type.FrameRate:F3} " +
                    $"range={_type.FullRange?.ToString() ?? "unset"} matrix={_type.Matrix} decodedByWindows={_type.DecodedByWindows}");
    }

    private unsafe void Request()
    {
        try
        {
            _reader!.ReadSample(VideoStream, 0, null, null, null, null);
        }
        catch (COMException e)
        {
            Fail(new CaptureError(e.HResult, "ReadSample"));
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            // Flush completes the outstanding ReadSample (OnFlush), so no callback runs once Close starts.
            _flushed.Reset();
            try
            {
                _reader?.Flush(AllStreams);
                _flushed.Wait(TimeSpan.FromSeconds(2));
            }
            catch (COMException)
            {
                // Already shut down (device gone): there is nothing in flight.
            }
            Close();
            MediaFoundation.Shutdown();
        }
    }

    private void Close()
    {
        MfInterop.Release(_reader);
        _reader = null;
        MfInterop.Shutdown(_source);
        MfInterop.Release(_source);
        _source = null;
        _callback = null;
        _decoder?.Dispose();
        _decoder = null;
    }

    public void Dispose() => Stop();

    private void Fail(CaptureError error)
    {
        if (!_running) return;
        _running = false;
        Log?.Invoke($"failed: {error}");
        Failed?.Invoke(error);
    }

    private unsafe void OnSample(HRESULT status, uint flags, long timestamp, IMFSample? sample)
    {
        try
        {
            if (status.Failed)
            {
                Fail(new CaptureError(status.Value, "ReadSample"));
                return;
            }
            var readerFlags = (MF_SOURCE_READER_FLAG)flags;
            if ((readerFlags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR) != 0)
            {
                Fail(new CaptureError(MfErrors.Shutdown, "ReadSample flags"));
                return;
            }
            if ((readerFlags & (MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_NATIVEMEDIATYPECHANGED
                                | MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED)) != 0)
                Log?.Invoke($"media type changed (flags 0x{flags:X})");
            if (sample != null && _running) Deliver(sample, timestamp);
        }
        finally
        {
            MfInterop.Release(sample);
        }
        if (_running) Request();
    }

    private unsafe void Deliver(IMFSample sample, long timestamp)
    {
        var type = _type!;
        Stats.Count(timestamp, _period);
        if (_decoder != null)
        {
            DeliverMjpeg(sample, timestamp);
            return;
        }
        sample.GetBufferByIndex(0, out var buffer);
        try
        {
            byte* data;
            int pitch;
            var twoD = buffer as IMF2DBuffer;
            if (twoD != null)
            {
                twoD.Lock2D(&data, out pitch);
            }
            else
            {
                buffer.Lock(&data, null, null);
                pitch = type.FourCC == "YUY2" ? type.Width * 2 : type.Width;
            }
            try
            {
                var format = ToPixelFormat(type.FourCC)!.Value;
                buffer.GetCurrentLength(out var length);
                var needed = (long)pitch * (format == PixelFormat.Nv12 ? type.Height * 3 / 2 : type.Height);
                if (twoD == null && length < needed)
                {
                    if (!_warnedShortBuffer) Log?.Invoke($"short buffer: {length} bytes, expected {needed}; frames dropped");
                    _warnedShortBuffer = true;
                    return;
                }
                var frame = new VideoFrame
                {
                    Format = format,
                    Width = type.Width,
                    Height = type.Height,
                    Plane0 = (nint)data,
                    Stride0 = pitch,
                    // NV12: the interleaved CbCr plane follows the luma rows at the same pitch.
                    Plane1 = format == PixelFormat.Nv12 ? (nint)(data + (long)pitch * type.Height) : 0,
                    Stride1 = format == PixelFormat.Nv12 ? pitch : 0,
                    FullRange = type.FullRange,
                    Matrix = type.Matrix,
                    Timestamp = timestamp,
                };
                FrameArrived?.Invoke(in frame);
            }
            finally
            {
                if (twoD != null) twoD.Unlock2D();
                else buffer.Unlock();
            }
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    /// <summary>
    /// Raw MJPG: decode on this MF thread into the decoder's reused planes, then hand the planar frame on. One
    /// ReadSample is in flight, so a slow decode delays the next read instead of queueing (newest frame wins).
    /// </summary>
    private unsafe void DeliverMjpeg(IMFSample sample, long timestamp)
    {
        sample.ConvertToContiguousBuffer(out var buffer);
        try
        {
            byte* data;
            uint length;
            buffer.Lock(&data, null, &length);
            try
            {
                CompressedFrameArrived?.Invoke(new ReadOnlySpan<byte>(data, (int)length), timestamp);
                if (!_decoder!.TryDecode(data, (nint)length, timestamp, out var frame))
                {
                    DecodeFailures++;
                    if (_decoder.LastError != _lastDecodeError) Log?.Invoke($"mjpeg decode failed: {_decoder.LastError}");
                    _lastDecodeError = _decoder.LastError;
                    return;
                }
                NoteDecode(_decoder.LastDecodeMilliseconds, frame);
                FrameArrived?.Invoke(in frame);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    /// <summary>Logs mean/max decode time every 600 frames (10 s at 60 fps).</summary>
    private void NoteDecode(double ms, in VideoFrame frame)
    {
        _decodeSum += ms;
        _decodeMax = Math.Max(_decodeMax, ms);
        if (++_decodeCount < 600) return;
        Log?.Invoke($"mjpeg decode {frame.Width}x{frame.Height} {frame.Format}: mean {_decodeSum / _decodeCount:F2} ms, " +
                    $"max {_decodeMax:F2} ms over {_decodeCount} frames, {_decoder!.Warnings} warnings, {DecodeFailures} failed");
        (_decodeCount, _decodeSum, _decodeMax) = (0, 0, 0);
    }

    private void OnMediaEvent(uint stream, IMFMediaEvent mediaEvent)
    {
        try
        {
            mediaEvent.GetType(out var type);
            HRESULT status;
            unsafe { mediaEvent.GetStatus(&status); }
            Log?.Invoke($"event {MediaEventName(type)} ({type}) stream {stream:X}: {MfErrors.Name(status.Value)}");
            if (status.Failed) Fail(new CaptureError(status.Value, $"OnEvent {MediaEventName(type)}"));
        }
        finally
        {
            MfInterop.Release(mediaEvent);
        }
    }

    internal static PixelFormat? ToPixelFormat(string fourCC) => fourCC switch
    {
        "NV12" => PixelFormat.Nv12,
        "YUY2" => PixelFormat.Yuy2,
        _ => null,
    };

    private static string MediaEventName(uint type) => type switch
    {
        1 => "MEError",
        800 => "MEVideoCaptureDeviceRemoved",
        801 => "MEVideoCaptureDevicePreempted",
        _ => "event",
    };

    /// <summary>The COM callback the reader holds. Built-in COM interop gives it a CCW (CsWin32 [ComImport] interface).</summary>
    private sealed class ReaderCallback(MfSource owner) : IMFSourceReaderCallback
    {
        public void OnReadSample(HRESULT hrStatus, uint dwStreamIndex, uint dwStreamFlags, long llTimestamp, IMFSample pSample) =>
            owner.OnSample(hrStatus, dwStreamFlags, llTimestamp, pSample);

        public void OnFlush(uint dwStreamIndex) => owner._flushed.Set();

        public void OnEvent(uint dwStreamIndex, IMFMediaEvent pEvent) => owner.OnMediaEvent(dwStreamIndex, pEvent);
    }
}
