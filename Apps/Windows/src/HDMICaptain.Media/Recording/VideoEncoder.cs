using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HDMICaptain.Media.Capture;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace HDMICaptain.Media.Recording;

/// <summary>An encoded sample handed to a sink, which owns (and releases) it.</summary>
internal delegate void EncodedHandler(IMFSample sample, long time, long duration, bool keyframe);

/// <summary>
/// H.264/HEVC encoding of pooled NV12 textures on its own thread (windows.md §8).
/// Hardware: the GPU vendor's async encoder MFT bound to the render device through an MFDXGIDeviceManager,
/// fed D3D11 surface samples (no readback). Software: the inbox sync H.264 MFT, fed from a staging copy.
/// No B-frames, a GOP of <see cref="RecorderOptions.KeyframeInterval"/> and a forced keyframe on the same
/// cadence, so Save Recent can cut at any IDR within that interval.
/// </summary>
internal sealed unsafe class VideoEncoder
{
    private const int MaxQueued = 16;

    private readonly Nv12Converter _converter;
    private readonly RecorderOptions _options;
    private readonly EncodedHandler _output;
    private readonly Action<Exception> _failed;
    private readonly BlockingCollection<Nv12Slot?> _input = new(MaxQueued);
    private readonly Queue<Nv12Slot> _inFlight = new();
    private readonly Thread _thread;
    private readonly long _frameDuration;
    private readonly long _keyframeInterval;
    private IMFActivate? _activate;
    private MftDriver? _driver;
    private ICodecAPI? _codec;
    private IMFDXGIDeviceManager? _manager;
    private bool _canForceKeyframe;
    private long _lastKeyframeRequest = long.MinValue;
    private long _lastOutputTime;

    public VideoEncoder(Nv12Converter converter, RecorderOptions options, EncodedHandler output, Action<Exception> failed)
    {
        _converter = converter;
        _options = options;
        _output = output;
        _failed = failed;
        _frameDuration = (long)(10_000_000 / Math.Max(1, options.FrameRate));
        _keyframeInterval = options.KeyframeInterval.Ticks;
        _thread = new Thread(Run) { IsBackground = true, Name = "Video encoder", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>The MFT's friendly name, e.g. "NVIDIA H.264 Encoder MFT".</summary>
    public string? Name { get; private set; }
    public bool IsHardware { get; private set; }
    public int Bitrate { get; private set; }

    /// <summary>Output type with MF_MT_MPEG_SEQUENCE_HEADER, ready for a passthrough sink writer once the first sample is out.</summary>
    public IMFMediaType? OutputType { get; private set; }

    /// <summary>Set once the MFT is configured (or failed to be).</summary>
    public ManualResetEventSlim Ready { get; } = new(false);

    public void Start() => _thread.Start();

    /// <summary>Queues a converted frame (the encoder releases the slot back to the converter).</summary>
    public void Enqueue(Nv12Slot slot)
    {
        try
        {
            if (_input.TryAdd(slot)) return;
        }
        catch (InvalidOperationException)
        {
            // completed
        }
        _converter.Release(slot);
    }

    /// <summary>Drains: everything queued is encoded and delivered, then the thread ends.</summary>
    public void Complete()
    {
        try
        {
            _input.Add(null);
            _input.CompleteAdding();
        }
        catch (InvalidOperationException)
        {
            // already completed
        }
    }

    public void Join() => _thread.Join();

    private void Run()
    {
        try
        {
            Initialize();
            Ready.Set();
            if (IsHardware) RunAsync();
            else RunSync();
        }
        catch (Exception e)
        {
            Ready.Set();
            _failed(e);
            foreach (var slot in _inFlight) _converter.Release(slot);
            _inFlight.Clear();
            foreach (var slot in _input.GetConsumingEnumerable())
                if (slot != null) _converter.Release(slot);
        }
        finally
        {
            Teardown();
        }
    }

    // MARK: - Setup

    private void Initialize()
    {
        var hevc = _options.Codec == VideoCodec.Hevc;
        var subtype = hevc ? PInvoke.MFVideoFormat_HEVC : PInvoke.MFVideoFormat_H264;
        var (vendor, warp) = AdapterOf(_converter.Device);
        var candidates = new List<(IMFActivate Activate, string Name, bool Hardware)>();
        if ((_options.PreferHardware || hevc) && !warp)
        {
            var tag = $"VEN_{vendor:X4}";
            foreach (var (activate, name) in MfMedia.Enumerate(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE,
                         PInvoke.MFMediaType_Video, PInvoke.MFVideoFormat_NV12, subtype))
            {
                // A hybrid PC lists every GPU's encoder; only the render adapter's can read our textures.
                if (string.Equals(activate.GetStringOrNull(PInvoke.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute), tag, StringComparison.OrdinalIgnoreCase))
                    candidates.Add((activate, name, true));
                else
                    MfInterop.Release(activate);
            }
        }
        if (!hevc)
            foreach (var (activate, name) in MfMedia.Enumerate(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT,
                         PInvoke.MFMediaType_Video, PInvoke.MFVideoFormat_NV12, subtype))
                candidates.Add((activate, name, false));

        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            if (_driver == null)
            {
                try
                {
                    Configure(candidate.Activate, candidate.Hardware, subtype, hevc);
                    (_activate, Name, IsHardware) = (candidate.Activate, candidate.Name, candidate.Hardware);
                    Trace.WriteLine($"VideoEncoder: {Name} ({(IsHardware ? "hardware, D3D11 input" : "software, CPU NV12")}) " +
                                    $"{_converter.Width}x{_converter.Height} {Bitrate / 1_000_000.0:F1} Mbps");
                    continue;
                }
                catch (COMException e)
                {
                    errors.Add($"{candidate.Name}: {MfErrors.Name(e.HResult)}");
                    Teardown();
                }
            }
            try
            {
                candidate.Activate.ShutdownObject();
            }
            catch (COMException)
            {
            }
            MfInterop.Release(candidate.Activate);
        }
        if (_driver == null)
            throw new InvalidOperationException(candidates.Count == 0
                ? $"No {(hevc ? "HEVC" : "H.264")} encoder on this PC."
                : "No encoder accepted the format: " + string.Join("; ", errors));
    }

    private void Configure(IMFActivate activate, bool hardware, Guid subtype, bool hevc)
    {
        var transform = MfMedia.ActivateTransform(activate);
        _driver = new MftDriver(transform);
        if (hardware)
        {
            transform.GetAttributes(out var attributes);
            try
            {
                if (attributes.GetUInt32OrNull(PInvoke.MF_TRANSFORM_ASYNC) == 1) attributes.SetUInt32(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            }
            finally
            {
                MfInterop.Release(attributes);
            }
            PInvoke.MFCreateDXGIDeviceManager(out var token, out var manager).ThrowOnFailure();
            _manager = manager;
            // Unique RCWs throughout: the cached RCW for a pointer is shared by every thread (two recorders share the
            // device), and the FinalRelease we do for prompt cleanup would tear it down under the other one.
            var device = Marshal.GetUniqueObjectForIUnknown(_converter.Device.NativePointer);
            try
            {
                manager.ResetDevice(device, token);
            }
            finally
            {
                MfInterop.Release(device);
            }
            var unknown = Marshal.GetIUnknownForObject(manager);
            try
            {
                transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)unknown);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        var (width, height) = (_converter.Width, _converter.Height);
        Bitrate = _options.BitrateFor(width, height, _options.BitsPerPixel ?? (hevc ? 0.10 : 0.15));
        var gop = (uint)Math.Max(1, Math.Round(_options.FrameRate * _options.KeyframeInterval.TotalSeconds));
        _codec = transform as ICodecAPI;
        if (_codec != null)
        {
            _codec.TrySet(typeof(CODECAPI_AVEncCommonRateControlMode).GUID,
                (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_UnconstrainedVBR);
            _codec.TrySet(typeof(CODECAPI_AVEncCommonMeanBitRate).GUID, (uint)Bitrate);
            _codec.TrySet(typeof(CODECAPI_AVEncMPVGOPSize).GUID, gop);
            _codec.TrySet(typeof(CODECAPI_AVEncMPVDefaultBPictureCount).GUID, 0u);
            _codec.TrySet(typeof(CODECAPI_AVLowLatencyMode).GUID, true);
            var force = typeof(CODECAPI_AVEncVideoForceKeyFrame).GUID;
            try
            {
                _codec.IsSupported(&force);
                _canForceKeyframe = true;
            }
            catch (COMException)
            {
                _canForceKeyframe = false;
            }
        }

        var (rateNum, rateDen) = Rational(_options.FrameRate);
        var output = MfMedia.CreateMediaType();
        try
        {
            output.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            output.SetGuid(PInvoke.MF_MT_SUBTYPE, subtype);
            output.SetUInt32(PInvoke.MF_MT_AVG_BITRATE, (uint)Bitrate);
            output.SetRatio(PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            output.SetRatio(PInvoke.MF_MT_FRAME_RATE, rateNum, rateDen);
            output.SetRatio(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            output.SetUInt32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            output.SetUInt32(PInvoke.MF_MT_MPEG2_PROFILE, hevc ? 1u : (uint)eAVEncH264VProfile.eAVEncH264VProfile_High);
            transform.SetOutputType(0, output, 0);
        }
        finally
        {
            MfInterop.Release(output);
        }

        var input = MfMedia.CreateMediaType();
        try
        {
            input.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            input.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
            input.SetRatio(PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            input.SetRatio(PInvoke.MF_MT_FRAME_RATE, rateNum, rateDen);
            input.SetRatio(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            input.SetUInt32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            input.SetUInt32(PInvoke.MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
            input.SetUInt32(PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
            transform.SetInputType(0, input, 0);
        }
        finally
        {
            MfInterop.Release(input);
        }

        _driver.ReadStreamInfo();
        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING);
        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM);
    }

    private void Teardown()
    {
        if (_driver != null)
        {
            try
            {
                _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING);
            }
            catch (COMException)
            {
            }
            MfInterop.Release(_driver.Transform);
        }
        if (_activate != null)
        {
            try
            {
                _activate.ShutdownObject(); // async MFTs must be shut down explicitly
            }
            catch (COMException)
            {
            }
            MfInterop.Release(_activate);
        }
        MfInterop.Release(_manager);
        (_driver, _codec, _manager, _activate) = (null, null, null, null);
    }

    private static (int Vendor, bool Warp) AdapterOf(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var desc = adapter.Description;
        return ((int)desc.VendorId, desc.VendorId == 0x1414 && desc.DeviceId == 0x8C);
    }

    private static (uint Num, uint Den) Rational(double fps)
    {
        if (Math.Abs(fps - Math.Round(fps)) < 0.001) return ((uint)Math.Round(fps), 1);
        return ((uint)Math.Round(fps * 1001), 1001); // 59.94, 29.97
    }

    // MARK: - Encoding

    private void RunSync()
    {
        foreach (var slot in _input.GetConsumingEnumerable())
        {
            if (slot == null) break;
            var time = slot.Time;
            IMFSample sample;
            try
            {
                sample = CpuSample(slot);
            }
            finally
            {
                _converter.Release(slot);
            }
            try
            {
                RequestKeyframeIfDue(time);
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
        _driver!.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM);
        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN);
        PullAll();
    }

    private void RunAsync()
    {
        var events = (IMFMediaEventGenerator)_driver!.Transform;
        var draining = false;
        while (true)
        {
            events.GetEvent(0, out var mediaEvent);
            uint type;
            HRESULT status;
            try
            {
                mediaEvent.GetType(out type);
                mediaEvent.GetStatus(&status);
            }
            finally
            {
                MfInterop.Release(mediaEvent);
            }
            if (status.Failed) throw new COMException($"encoder event {type}", status.Value);

            switch (type)
            {
                case MfMedia.METransformNeedInput when !draining:
                    if (!_input.TryTake(out var slot, Timeout.Infinite) || slot == null)
                    {
                        draining = true;
                        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM);
                        _driver.Message(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN);
                        break;
                    }
                    RequestKeyframeIfDue(slot.Time);
                    _inFlight.Enqueue(slot);
                    var sample = GpuSample(slot);
                    try
                    {
                        _driver.Transform.ProcessInput(0, sample, 0);
                    }
                    finally
                    {
                        MfInterop.Release(sample);
                    }
                    break;
                case MfMedia.METransformHaveOutput:
                    if (_driver.Pull(TypeChanged) is { } output)
                    {
                        var time = Deliver(output);
                        // No B-frames: once frame t is out, every input up to t has been consumed.
                        while (_inFlight.Count > 0 && _inFlight.Peek().Time <= time) _converter.Release(_inFlight.Dequeue());
                    }
                    break;
                case MfMedia.METransformDrainComplete:
                    foreach (var s in _inFlight) _converter.Release(s);
                    _inFlight.Clear();
                    return;
                case MfMedia.MEError:
                    throw new COMException("encoder error event", status.Value);
            }
        }
    }

    private void PullAll()
    {
        while (_driver!.Pull(TypeChanged) is { } sample) Deliver(sample);
    }

    private void TypeChanged()
    {
        MfInterop.Release(OutputType);
        OutputType = null;
    }

    private long Deliver(IMFSample sample)
    {
        var time = MfMedia.TimeOrNull(sample) ?? _lastOutputTime + _frameDuration;
        var duration = MfMedia.DurationOrNull(sample) is { } d && d > 0 ? d : _frameDuration;
        var keyframe = MfMedia.IsKeyframe(sample);
        _lastOutputTime = time;
        if (OutputType == null) OutputType = WriterType(sample, keyframe);
        _output(sample, time, duration, keyframe);
        return time;
    }

    /// <summary>The current output type, plus the parameter sets from the first keyframe if the MFT left them out.</summary>
    private IMFMediaType WriterType(IMFSample sample, bool keyframe)
    {
        _driver!.Transform.GetOutputCurrentType(0, out var current);
        try
        {
            var type = MfMedia.Clone(current);
            if (type.GetBlobOrNull(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER) == null && keyframe
                && MfMedia.ParameterSets(MfMedia.CopyBytes(sample), _options.Codec == VideoCodec.Hevc) is { } sets)
                type.SetBlob(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER, sets);
            return type;
        }
        finally
        {
            MfInterop.Release(current);
        }
    }

    private void RequestKeyframeIfDue(long time)
    {
        if (_lastKeyframeRequest != long.MinValue && time - _lastKeyframeRequest < _keyframeInterval) return;
        _lastKeyframeRequest = time;
        if (_canForceKeyframe) _codec!.TrySet(typeof(CODECAPI_AVEncVideoForceKeyFrame).GUID, 1u);
    }

    /// <summary>A D3D11 surface sample over the slot's texture: the hardware MFT reads it on the GPU.</summary>
    private IMFSample GpuSample(Nv12Slot slot)
    {
        var surface = Marshal.GetUniqueObjectForIUnknown(slot.Texture.NativePointer);
        IMFMediaBuffer buffer;
        try
        {
            PInvoke.MFCreateDXGISurfaceBuffer(MfMedia.IidTexture2D, surface, 0, false, out buffer).ThrowOnFailure();
        }
        finally
        {
            MfInterop.Release(surface);
        }
        try
        {
            if (buffer is IMF2DBuffer twoD)
            {
                twoD.GetContiguousLength(out var length);
                buffer.SetCurrentLength(length);
            }
            return MfMedia.Wrap(buffer, slot.Time, _frameDuration);
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }

    /// <summary>Copies the slot to a staging texture and into a contiguous NV12 memory buffer for the software MFT.</summary>
    private IMFSample CpuSample(Nv12Slot slot)
    {
        var (width, height) = (_converter.Width, _converter.Height);
        var context = _converter.Context;
        slot.Staging ??= _converter.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        context.CopyResource(slot.Staging, slot.Texture);
        var size = width * height * 3 / 2;
        PInvoke.MFCreateMemoryBuffer((uint)size, out var buffer).ThrowOnFailure();
        try
        {
            var mapped = context.Map(slot.Staging, 0, MapMode.Read, MapFlags.None);
            try
            {
                byte* target;
                buffer.Lock(&target, null, null);
                var pitch = (int)mapped.RowPitch;
                var source = (byte*)mapped.DataPointer;
                for (var row = 0; row < height; row++)
                    Buffer.MemoryCopy(source + (long)row * pitch, target + (long)row * width, width, width);
                var chroma = source + (long)pitch * height;
                var chromaTarget = target + (long)width * height;
                for (var row = 0; row < height / 2; row++)
                    Buffer.MemoryCopy(chroma + (long)row * pitch, chromaTarget + (long)row * width, width, width);
                buffer.Unlock();
            }
            finally
            {
                context.Unmap(slot.Staging, 0);
            }
            buffer.SetCurrentLength((uint)size);
            return MfMedia.Wrap(buffer, slot.Time, _frameDuration);
        }
        finally
        {
            MfInterop.Release(buffer);
        }
    }
}
