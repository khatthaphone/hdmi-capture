using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Luid = Vortice.Luid;
using PixelFormat = HDMICaptain.Media.Video.PixelFormat;

namespace HDMICaptain.Media.Render;

/// <summary>
/// The MetalRenderer equivalent on D3D11 (spec §13.4). Frames are rendered on the caller's thread (the source's),
/// in two passes like Android: pass 1 converts the planes to RGBA at source size (<see cref="SourceTexture"/>,
/// the clean picture for screenshots and recordings); pass 2 scales it into each swap chain and presents.
/// The device lives on the adapter that drives the window's monitor and is rebuilt on device loss or adapter change.
/// </summary>
public sealed class D3DRenderer : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SwapChainTarget> _targets = [];
    private readonly bool _warp;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _psConvert = null!;
    private ID3D11PixelShader _psScale = null!;
    private ID3D11Buffer _params = null!;
    private ID3D11SamplerState _linear = null!;
    private ID3D11SamplerState _nearest = null!;
    private ID3D11Texture3D _lutTexture = null!;
    private ID3D11ShaderResourceView _lutView = null!;
    private (float Intensity, int Size, Vector3 Min, Vector3 Max) _lut;

    private readonly PlaneTexture[] _planes = [new(), new(), new()];
    private ID3D11Texture2D? _source;
    private ID3D11RenderTargetView? _sourceTarget;
    private ID3D11ShaderResourceView? _sourceView;

    private D3DRenderer(bool warp, Luid? adapter)
    {
        _warp = warp;
        CreateDevice(adapter);
    }

    /// <summary>A renderer on the GPU driving <paramref name="hwnd"/>'s monitor (or the default adapter if 0).</summary>
    public static D3DRenderer Create(nint hwnd) => new(false, AdapterForWindow(hwnd));

    /// <summary>A software (WARP) renderer for tests and machines without a usable GPU.</summary>
    public static D3DRenderer CreateWarp() => new(true, null);

    public ID3D11Device Device => _device;
    public Luid AdapterLuid { get; private set; }
    public string AdapterName { get; private set; } = "";

    /// <summary>Scale mode, filter, range and matrix; read at the next render.</summary>
    public RenderOptions Options { get; set; } = new();

    /// <summary>
    /// Brightness, contrast, saturation, gamma (identity = 0, 1, 1, 1) and sharpness, as MetalRenderer's ImageAdjustments.
    /// Gamma is the user value (0.5…2); the shader gets 1/gamma.
    /// </summary>
    public (float Brightness, float Contrast, float Saturation, float Gamma, float Sharpness) Adjustments { get; set; } = (0, 1, 1, 1, 0);

    /// <summary>Size of the last converted frame (0 × 0 before the first).</summary>
    public (int Width, int Height) SourceSize { get; private set; }

    /// <summary>Pass-1 output (RGBA8 at source size). Overlays never touch it (CLAUDE.md output rule).</summary>
    public ID3D11Texture2D? SourceTexture => _source;

    /// <summary>Raised (on the render thread) after a lost device was rebuilt.</summary>
    public event Action? DeviceRecreated;

    public SwapChainTarget CreateHwndTarget(nint hwnd, int width, int height)
    {
        var target = new SwapChainTarget(hwnd, null, width, height);
        lock (_gate)
        {
            CreateSwapChain(target);
            _targets.Add(target);
        }
        return target;
    }

    /// <summary>
    /// A composition swap chain for a XAML <c>SwapChainPanel</c>; <paramref name="attach"/> sets it on the panel
    /// (marshal to the UI thread there). It is called again whenever the device is recreated.
    /// </summary>
    public SwapChainTarget CreateCompositionTarget(int width, int height, Action<IDXGISwapChain1> attach)
    {
        var target = new SwapChainTarget(0, attach, width, height);
        lock (_gate)
        {
            CreateSwapChain(target);
            _targets.Add(target);
        }
        return target;
    }

    public void RemoveTarget(SwapChainTarget target)
    {
        lock (_gate)
        {
            _targets.Remove(target);
            target.Dispose();
        }
    }

    /// <summary>Converts <paramref name="frame"/> and presents it on every visible target.</summary>
    public void Render(in VideoFrame frame)
    {
        lock (_gate)
        {
            Upload(frame);
            Convert(frame);
            PresentAll();
        }
    }

    /// <summary>Re-presents the last converted frame (after a resize while the source is paused).</summary>
    public void Redraw()
    {
        lock (_gate)
        {
            if (_sourceView != null) PresentAll();
        }
    }

    /// <summary>Forgets the last frame and presents black (source lost), so a stale picture never lingers.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _sourceView?.Dispose();
            _sourceTarget?.Dispose();
            _source?.Dispose();
            (_sourceView, _sourceTarget, _source) = (null, null, null);
            SourceSize = default;
            PresentAll();
        }
    }

    /// <summary>Uploads a 3D LUT (size³ RGB triples, red fastest, as .cube files list them) applied at <paramref name="intensity"/>.</summary>
    public void SetLut(int size, ReadOnlySpan<float> rgb, Vector3 domainMin, Vector3 domainMax, float intensity)
    {
        lock (_gate)
        {
            var rgba = new float[size * size * size * 4];
            for (var i = 0; i < size * size * size; i++)
            {
                rgba[i * 4] = rgb[i * 3];
                rgba[i * 4 + 1] = rgb[i * 3 + 1];
                rgba[i * 4 + 2] = rgb[i * 3 + 2];
                rgba[i * 4 + 3] = 1;
            }
            CreateLutTexture(size, rgba);
            _lut = (intensity, size, domainMin, domainMax);
        }
    }

    public void ClearLut()
    {
        lock (_gate)
        {
            CreateLutTexture(2, IdentityLut2);
            _lut = default;
        }
    }

    /// <summary>Copies <see cref="SourceTexture"/> to the CPU as tightly packed RGBA8 (screenshots, tests).</summary>
    public byte[]? ReadSourcePixels()
    {
        lock (_gate)
        {
            if (_source == null) return null;
            var (w, h) = SourceSize;
            var desc = _source.Description with
            {
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read, MiscFlags = ResourceOptionFlags.None,
            };
            using var staging = _device.CreateTexture2D(desc);
            _context.CopyResource(staging, _source);
            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[w * h * 4];
                for (var row = 0; row < h; row++)
                    Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, pixels, row * w * 4, w * 4);
                return pixels;
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
    }

    /// <summary>Recreates the device when <paramref name="hwnd"/> now sits on a monitor driven by another adapter.</summary>
    public void CheckAdapter(nint hwnd)
    {
        if (_warp) return;
        var luid = AdapterForWindow(hwnd);
        lock (_gate)
        {
            if (luid is { } l && !l.Equals(AdapterLuid)) RecreateDevice(l);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var t in _targets) t.Dispose();
            _targets.Clear();
            ReleaseDevice();
        }
    }

    // MARK: - Device

    private static readonly float[] IdentityLut2 = BuildIdentityLut2();

    private static float[] BuildIdentityLut2()
    {
        var data = new float[2 * 2 * 2 * 4];
        for (var b = 0; b < 2; b++)
            for (var g = 0; g < 2; g++)
                for (var r = 0; r < 2; r++)
                {
                    var i = ((b * 2 + g) * 2 + r) * 4;
                    (data[i], data[i + 1], data[i + 2], data[i + 3]) = (r, g, b, 1);
                }
        return data;
    }

    private void CreateDevice(Luid? adapterLuid)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        IDXGIAdapter1? adapter = null;
        if (_warp)
            factory.EnumWarpAdapter(out adapter).CheckError();
        else if (adapterLuid is { } luid)
            factory.EnumAdapterByLuid(luid, out adapter);

        ID3D11Device? device;
        ID3D11DeviceContext? context;
        var result = D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown, flags, levels,
            out device, out context);
        if (result.Failure && adapter != null)
        {
            // e.g. an indirect-display adapter that can't render: fall back to the default GPU.
            adapter!.Dispose();
            adapter = null;
            result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out device, out context);
        }
        result.CheckError();
        _device = device!;
        _context = context!;

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
        using (var actual = dxgiDevice.GetAdapter())
        {
            AdapterLuid = actual.Description.Luid;
            AdapterName = actual.Description.Description;
        }
        adapter?.Dispose();

        var vs = Compiler.Compile(Shaders.Source, "VSQuad", "HDMICaptain.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _vs = _device.CreateVertexShader(vs.Span);
        _psConvert = _device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSConvert", "HDMICaptain.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        _psScale = _device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSScale", "HDMICaptain.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        _params = _device.CreateBuffer((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _linear = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _nearest = _device.CreateSamplerState(SamplerDescription.PointClamp);
        CreateLutTexture(2, IdentityLut2);
    }

    private void CreateLutTexture(int size, float[] rgba)
    {
        _lutView?.Dispose();
        _lutTexture?.Dispose();
        var desc = new Texture3DDescription(Format.R32G32B32A32_Float, (uint)size, (uint)size, (uint)size, 1, BindFlags.ShaderResource);
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(size * 16), (uint)(size * size * 16));
            _lutTexture = _device.CreateTexture3D(desc, [data]);
        }
        finally
        {
            handle.Free();
        }
        _lutView = _device.CreateShaderResourceView(_lutTexture);
    }

    private void ReleaseDevice()
    {
        foreach (var p in _planes) p.Release();
        _sourceView?.Dispose();
        _sourceTarget?.Dispose();
        _source?.Dispose();
        (_sourceView, _sourceTarget, _source) = (null, null, null);
        SourceSize = default;
        _lutView?.Dispose();
        _lutTexture?.Dispose();
        _linear?.Dispose();
        _nearest?.Dispose();
        _params?.Dispose();
        _psScale?.Dispose();
        _psConvert?.Dispose();
        _vs?.Dispose();
        _context?.ClearState();
        _context?.Flush();
        _context?.Dispose();
        _device?.Dispose();
    }

    private void RecreateDevice(Luid? adapter)
    {
        Trace.WriteLine($"D3DRenderer: recreating device (removed reason {_device?.DeviceRemovedReason})");
        foreach (var t in _targets) t.ReleaseDeviceObjects();
        ReleaseDevice();
        CreateDevice(adapter);
        if (_lut.Size == 0) _lut = default;
        foreach (var t in _targets) CreateSwapChain(t);
        DeviceRecreated?.Invoke();
    }

    private static unsafe Luid? AdapterForWindow(nint hwnd)
    {
        if (hwnd == 0) return null;
        var monitor = PInvoke.MonitorFromWindow((HWND)hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            using (adapter)
            {
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                    using (output)
                        if (output.Description.Monitor == (nint)monitor.Value)
                            return adapter.Description1.Luid;
            }
        return null;
    }

    // MARK: - Swap chains

    private void CreateSwapChain(SwapChainTarget target)
    {
        target.TakePendingResize(out var width, out var height, out var scale);
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var tearing = false;
        using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
            tearing = factory5?.PresentAllowTearing == true;

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = target.IsComposition ? Scaling.Stretch : Scaling.None,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = target.IsComposition ? AlphaMode.Premultiplied : AlphaMode.Ignore,
            Flags = SwapChainFlags.FrameLatencyWaitableObject | (tearing ? SwapChainFlags.AllowTearing : 0),
        };

        IDXGISwapChain1 chain;
        if (target.IsComposition)
        {
            try
            {
                chain = factory.CreateSwapChainForComposition(_device, desc);
            }
            catch (SharpGen.Runtime.SharpGenException) when (tearing)
            {
                tearing = false;
                chain = factory.CreateSwapChainForComposition(_device, desc with { Flags = SwapChainFlags.FrameLatencyWaitableObject });
            }
        }
        else
        {
            chain = factory.CreateSwapChainForHwnd(_device, target.Hwnd, desc);
            factory.MakeWindowAssociation(target.Hwnd, WindowAssociationFlags.IgnoreAltEnter);
        }

        using (chain)
        {
            target.SwapChain = chain.QueryInterface<IDXGISwapChain2>();
        }
        target.Tearing = tearing;
        target.SwapChain.MaximumFrameLatency = 1;
        target.Waitable = target.SwapChain.FrameLatencyWaitableObject;
        if (target.IsComposition) ApplyCompositionScale(target, scale);
        CreateTargetView(target);
        target.Attach?.Invoke(target.SwapChain);
    }

    private static void ApplyCompositionScale(SwapChainTarget target, float scale)
    {
        var inverse = Matrix3x2.CreateScale(1 / scale);
        target.SwapChain!.MatrixTransform = inverse;
    }

    private void CreateTargetView(SwapChainTarget target)
    {
        using var buffer = target.SwapChain!.GetBuffer<ID3D11Texture2D>(0);
        target.View = _device.CreateRenderTargetView(buffer);
    }

    private void ApplyResize(SwapChainTarget target)
    {
        if (!target.TakePendingResize(out var width, out var height, out var scale)) return;
        target.View?.Dispose();
        target.View = null;
        _context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
        _context.Flush();
        var flags = SwapChainFlags.FrameLatencyWaitableObject | (target.Tearing ? SwapChainFlags.AllowTearing : 0);
        target.SwapChain!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, flags).CheckError();
        if (target.IsComposition) ApplyCompositionScale(target, scale);
        CreateTargetView(target);
    }

    // MARK: - Frames

    private void Upload(in VideoFrame frame)
    {
        var (cw, ch) = frame.ChromaSize;
        switch (frame.Format)
        {
            case PixelFormat.Nv12:
                _planes[0].Upload(_device, _context, Format.R8_UNorm, frame.Width, frame.Height, frame.Plane0, frame.Stride0, frame.Width);
                _planes[1].Upload(_device, _context, Format.R8G8_UNorm, cw, ch, frame.Plane1, frame.Stride1, cw * 2);
                break;
            case PixelFormat.Yuy2:
                _planes[0].Upload(_device, _context, Format.R8G8B8A8_UNorm, cw, frame.Height, frame.Plane0, frame.Stride0, cw * 4);
                break;
            case PixelFormat.I420 or PixelFormat.I422 or PixelFormat.I444:
                _planes[0].Upload(_device, _context, Format.R8_UNorm, frame.Width, frame.Height, frame.Plane0, frame.Stride0, frame.Width);
                _planes[1].Upload(_device, _context, Format.R8_UNorm, cw, ch, frame.Plane1, frame.Stride1, cw);
                _planes[2].Upload(_device, _context, Format.R8_UNorm, cw, ch, frame.Plane2, frame.Stride2, cw);
                break;
            case PixelFormat.Bgra:
                _planes[0].Upload(_device, _context, Format.B8G8R8A8_UNorm, frame.Width, frame.Height, frame.Plane0, frame.Stride0, frame.Width * 4);
                break;
        }
    }

    private void Convert(in VideoFrame frame)
    {
        if (_source == null || SourceSize != (frame.Width, frame.Height))
        {
            _sourceView?.Dispose();
            _sourceTarget?.Dispose();
            _source?.Dispose();
            _source = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)frame.Width, (uint)frame.Height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            _sourceTarget = _device.CreateRenderTargetView(_source);
            _sourceView = _device.CreateShaderResourceView(_source);
            SourceSize = (frame.Width, frame.Height);
        }

        var options = Options;
        var fullRange = options.ColorRange switch
        {
            ColorRange.Full => true,
            ColorRange.Limited => false,
            _ => frame.FullRange ?? false,
        };
        var bt709 = options.Matrix switch
        {
            ColorMatrix.Bt601 => false,
            ColorMatrix.Bt709 => true,
            _ => frame.Matrix switch { ColorMatrix.Bt601 => false, ColorMatrix.Bt709 => true, _ => frame.Height >= 720 },
        };
        var p = ShaderParams.Make(fullRange, bt709);
        var a = Adjustments;
        p.Adjust = new Vector4(a.Brightness, a.Contrast, a.Saturation, 1 / Math.Max(0.1f, a.Gamma));
        p.Sharpen = new Vector4(a.Sharpness, 0, 0, 0);
        p.Lut = new Vector4(_lut.Intensity, Math.Max(2, _lut.Size), 0, 0);
        p.LutMin = new Vector4(_lut.Min, 0);
        p.LutMax = _lut.Size == 0 ? new Vector4(1, 1, 1, 0) : new Vector4(_lut.Max, 0);
        p.Input = frame.Format switch
        {
            PixelFormat.Nv12 => 0,
            PixelFormat.Yuy2 => 1,
            PixelFormat.Bgra => 3,
            _ => 2,
        };
        WriteParams(p);

        _context.OMSetRenderTargets(_sourceTarget!);
        _context.RSSetViewport(0, 0, frame.Width, frame.Height);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        _context.VSSetShader(_vs);
        _context.VSSetConstantBuffer(0, _params);
        _context.PSSetShader(_psConvert);
        _context.PSSetConstantBuffer(0, _params);
        _context.PSSetShaderResources(0, [_planes[0].View!, _planes[1].View!, _planes[2].View!, _lutView]);
        _context.PSSetSamplers(0, [_linear, _linear]);
        _context.Draw(4, 0);
        _context.PSSetShaderResources(0, [null!, null!, null!, null!]);
    }

    private void PresentAll()
    {
        foreach (var target in _targets)
        {
            if (!target.IsVisible || target.SwapChain == null) continue;
            ApplyResize(target);
            // Max latency 1: wait until the swap chain can take a frame, so we never queue behind the display.
            if (target.Waitable != 0) PInvoke.WaitForSingleObject((HANDLE)target.Waitable, 100);
            DrawScaled(target);
            var flags = target.Tearing ? PresentFlags.AllowTearing : PresentFlags.None;
            var result = target.SwapChain.Present(0, flags);
            if (result.Code == Vortice.DXGI.ResultCode.DeviceRemoved.Code || result.Code == Vortice.DXGI.ResultCode.DeviceReset.Code)
            {
                RecreateDevice(_warp ? null : AdapterLuid);
                return;
            }
        }
    }

    private void DrawScaled(SwapChainTarget target)
    {
        if (_sourceView == null)
        {
            _context.OMSetRenderTargets(target.View!);
            _context.ClearRenderTargetView(target.View!, new Color4(0, 0, 0, 1));
            return;
        }
        var options = Options;
        var (sw, sh) = SourceSize;
        var g = RenderGeometry.Compute(sw, sh, target.Width, target.Height, options.Scale);
        var p = ShaderParams.Make(true, true);
        p.UvRect = new Vector4((float)g.SourceRect.X, (float)g.SourceRect.Y,
            (float)(g.SourceRect.X + g.SourceRect.Width), (float)(g.SourceRect.Y + g.SourceRect.Height));
        WriteParams(p);

        _context.OMSetRenderTargets(target.View!);
        _context.ClearRenderTargetView(target.View!, new Color4(0, 0, 0, 1));
        _context.RSSetViewport((float)g.Viewport.X, (float)g.Viewport.Y, (float)g.Viewport.Width, (float)g.Viewport.Height);
        _context.PSSetShader(_psScale);
        _context.PSSetShaderResource(0, _sourceView!);
        _context.PSSetSampler(0, options.Filter == FilterMode.Nearest ? _nearest : _linear);
        _context.Draw(4, 0);
        _context.PSSetShaderResource(0, null!);
    }

    private void WriteParams(in ShaderParams p)
    {
        var mapped = _context.Map(_params, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        _context.Unmap(_params, 0);
    }

    /// <summary>A dynamic texture for one source plane, recreated when size or format changes.</summary>
    private sealed class PlaneTexture
    {
        private ID3D11Texture2D? _texture;
        private (Format Format, int W, int H) _shape;

        public ID3D11ShaderResourceView? View { get; private set; }

        public unsafe void Upload(ID3D11Device device, ID3D11DeviceContext context, Format format, int width, int height,
            nint data, int stride, int rowBytes)
        {
            if (_texture == null || _shape != (format, width, height))
            {
                Release();
                _texture = device.CreateTexture2D(new Texture2DDescription(format, (uint)width, (uint)height, 1, 1,
                    BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
                View = device.CreateShaderResourceView(_texture);
                _shape = (format, width, height);
            }
            var mapped = context.Map(_texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                if (mapped.RowPitch == stride && stride == rowBytes)
                    Buffer.MemoryCopy((void*)data, (void*)mapped.DataPointer, (long)mapped.RowPitch * height, (long)rowBytes * height);
                else
                    for (var row = 0; row < height; row++)
                        Buffer.MemoryCopy((void*)(data + row * stride), (void*)(mapped.DataPointer + row * (int)mapped.RowPitch), rowBytes, rowBytes);
            }
            finally
            {
                context.Unmap(_texture, 0);
            }
        }

        public void Release()
        {
            View?.Dispose();
            _texture?.Dispose();
            (View, _texture) = (null, null);
        }
    }
}
