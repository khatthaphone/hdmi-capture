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
using Rect = HDMICaptain.Core.Rect;
using PixelFormat = HDMICaptain.Media.Video.PixelFormat;

namespace HDMICaptain.Media.Render;

/// <summary>
/// One D3D11 device with the shaders, samplers and LUT both renderers draw with: <see cref="D3DRenderer"/> (one card,
/// any number of swap chains) and <see cref="GridRenderer"/> (many cards, one swap chain). Not thread-safe; owners
/// call it under their own lock. Rebuilt as a whole on device loss.
/// </summary>
internal sealed class RenderDevice : IDisposable
{
    private static readonly ID3D11ShaderResourceView[] NoViews = new ID3D11ShaderResourceView[4];
    private static readonly float[] IdentityLut2 = BuildIdentityLut2();

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psConvert;
    private readonly ID3D11PixelShader _psScale;
    private readonly ID3D11Buffer _params;
    private readonly ID3D11SamplerState _linear;
    private readonly ID3D11SamplerState _nearest;
    private readonly ID3D11ShaderResourceView[] _convertViews = new ID3D11ShaderResourceView[4];
    private readonly ID3D11SamplerState[] _convertSamplers = new ID3D11SamplerState[2];
    private ID3D11Texture3D _lutTexture = null!;
    private ID3D11ShaderResourceView _lutView = null!;
    private (float Intensity, int Size, Vector3 Min, Vector3 Max) _lut;

    public RenderDevice(bool warp, Luid? adapterLuid)
    {
        Warp = warp;
        // Video support lets hardware encoder MFTs take our textures (task 100); WARP has no video engine.
        var flags = DeviceCreationFlags.BgraSupport | (warp ? 0 : DeviceCreationFlags.VideoSupport);
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        IDXGIAdapter1? adapter = null;
        if (warp)
            factory.EnumWarpAdapter(out adapter).CheckError();
        else if (adapterLuid is { } luid)
            factory.EnumAdapterByLuid(luid, out adapter);

        ID3D11Device? device;
        ID3D11DeviceContext? context;
        var result = D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown, flags, levels,
            out device, out context);
        if (result.Failure && (flags & DeviceCreationFlags.VideoSupport) != 0)
        {
            flags &= ~DeviceCreationFlags.VideoSupport;
            result = D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown, flags, levels,
                out device, out context);
        }
        if (result.Failure && adapter != null)
        {
            // e.g. an indirect-display adapter that can't render: fall back to the default GPU.
            adapter!.Dispose();
            adapter = null;
            result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, levels, out device, out context);
        }
        result.CheckError();
        Device = device!;
        Context = context!;

        using (var dxgiDevice = Device.QueryInterface<IDXGIDevice>())
        using (var actual = dxgiDevice.GetAdapter())
        {
            AdapterLuid = actual.Description.Luid;
            AdapterName = actual.Description.Description;
        }
        adapter?.Dispose();

        var vs = Compiler.Compile(Shaders.Source, "VSQuad", "HDMICaptain.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _vs = Device.CreateVertexShader(vs.Span);
        _psConvert = Device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSConvert", "HDMICaptain.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        _psScale = Device.CreatePixelShader(Compiler.Compile(Shaders.Source, "PSScale", "HDMICaptain.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None).Span);
        _params = Device.CreateBuffer((uint)Marshal.SizeOf<ShaderParams>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _linear = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        _nearest = Device.CreateSamplerState(SamplerDescription.PointClamp);
        CreateLutTexture(2, IdentityLut2);
    }

    public bool Warp { get; }
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public Luid AdapterLuid { get; }
    public string AdapterName { get; }

    /// <summary>The adapter that drives <paramref name="hwnd"/>'s monitor, or null (default adapter).</summary>
    public static unsafe Luid? AdapterForWindow(nint hwnd)
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

    // MARK: - LUT

    /// <summary>Uploads a 3D LUT (size³ RGBA, red fastest) applied at <paramref name="intensity"/>.</summary>
    public void SetLut(int size, float[] rgba, Vector3 domainMin, Vector3 domainMax, float intensity)
    {
        CreateLutTexture(size, rgba);
        _lut = (intensity, size, domainMin, domainMax);
    }

    public void ClearLut()
    {
        CreateLutTexture(2, IdentityLut2);
        _lut = default;
    }

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

    private void CreateLutTexture(int size, float[] rgba)
    {
        _lutView?.Dispose();
        _lutTexture?.Dispose();
        var desc = new Texture3DDescription(Format.R32G32B32A32_Float, (uint)size, (uint)size, (uint)size, 1, BindFlags.ShaderResource);
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(size * 16), (uint)(size * size * 16));
            _lutTexture = Device.CreateTexture3D(desc, [data]);
        }
        finally
        {
            handle.Free();
        }
        _lutView = Device.CreateShaderResourceView(_lutTexture);
    }

    // MARK: - Swap chains

    public void CreateSwapChain(SwapChainTarget target)
    {
        target.TakePendingResize(out var width, out var height, out var scale);
        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
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
                chain = factory.CreateSwapChainForComposition(Device, desc);
            }
            catch (SharpGen.Runtime.SharpGenException) when (tearing)
            {
                tearing = false;
                chain = factory.CreateSwapChainForComposition(Device, desc with { Flags = SwapChainFlags.FrameLatencyWaitableObject });
            }
        }
        else
        {
            chain = factory.CreateSwapChainForHwnd(Device, target.Hwnd, desc);
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
        target.View = Device.CreateRenderTargetView(buffer);
    }

    /// <summary>Applies a pending <see cref="SwapChainTarget.Resize"/>, then (optionally) waits until the chain can take a frame.</summary>
    public void PrepareTarget(SwapChainTarget target, bool waitForFrame = true)
    {
        if (target.TakePendingResize(out var width, out var height, out var scale))
        {
            target.View?.Dispose();
            target.View = null;
            Context.OMSetRenderTargets((ID3D11RenderTargetView)null!, null);
            Context.Flush();
            var flags = SwapChainFlags.FrameLatencyWaitableObject | (target.Tearing ? SwapChainFlags.AllowTearing : 0);
            target.SwapChain!.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, flags).CheckError();
            if (target.IsComposition) ApplyCompositionScale(target, scale);
            CreateTargetView(target);
        }
        // Max latency 1: wait until the swap chain can take a frame, so we never queue behind the display.
        if (waitForFrame && target.Waitable != 0) PInvoke.WaitForSingleObject((HANDLE)target.Waitable, 100);
    }

    /// <summary>Presents; false when the device was lost and must be rebuilt.</summary>
    public bool Present(SwapChainTarget target)
    {
        var flags = target.Tearing ? PresentFlags.AllowTearing : PresentFlags.None;
        var result = target.SwapChain!.Present(0, flags);
        return result.Code != Vortice.DXGI.ResultCode.DeviceRemoved.Code && result.Code != Vortice.DXGI.ResultCode.DeviceReset.Code;
    }

    // MARK: - Drawing

    /// <summary>Pass 1: converts <paramref name="frame"/> into <paramref name="surface"/>'s RGBA texture.</summary>
    public void Convert(SourceSurface surface, in VideoFrame frame, RenderOptions options,
        (float Brightness, float Contrast, float Saturation, float Gamma, float Sharpness) adjustments)
    {
        surface.Upload(this, frame);
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
        var a = adjustments;
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

        Context.OMSetRenderTargets(surface.Target!);
        Context.RSSetViewport(0, 0, frame.Width, frame.Height);
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        Context.VSSetShader(_vs);
        Context.VSSetConstantBuffer(0, _params);
        Context.PSSetShader(_psConvert);
        Context.PSSetConstantBuffer(0, _params);
        // Reused arrays: this runs per frame and must not allocate (task 101).
        var planes = surface.Planes;
        (_convertViews[0], _convertViews[1], _convertViews[2], _convertViews[3]) = (planes[0].View!, planes[1].View!, planes[2].View!, _lutView);
        (_convertSamplers[0], _convertSamplers[1]) = (_linear, _linear);
        Context.PSSetShaderResources(0, _convertViews);
        Context.PSSetSamplers(0, _convertSamplers);
        Context.Draw(4, 0);
        Context.PSSetShaderResources(0, NoViews);
    }

    /// <summary>Binds <paramref name="target"/>'s back buffer and clears it to black.</summary>
    public void BeginTarget(SwapChainTarget target)
    {
        Context.OMSetRenderTargets(target.View!);
        Context.ClearRenderTargetView(target.View!, new Color4(0, 0, 0, 1));
    }

    /// <summary>Pass 2: draws <paramref name="surface"/> into <paramref name="cell"/> (pixels) of the bound target.</summary>
    public void DrawScaled(SourceSurface surface, Rect cell, ScaleMode scale, FilterMode filter)
    {
        if (surface.View == null) return;
        var (sw, sh) = surface.Size;
        var g = RenderGeometry.Compute(sw, sh, cell.Width, cell.Height, scale);
        var p = ShaderParams.Make(true, true);
        p.UvRect = new Vector4((float)g.SourceRect.X, (float)g.SourceRect.Y,
            (float)(g.SourceRect.X + g.SourceRect.Width), (float)(g.SourceRect.Y + g.SourceRect.Height));
        WriteParams(p);

        Context.RSSetViewport((float)(cell.X + g.Viewport.X), (float)(cell.Y + g.Viewport.Y), (float)g.Viewport.Width, (float)g.Viewport.Height);
        // Bind everything this pass needs: SourceRendered handlers draw on the same context between frames.
        Context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        Context.VSSetShader(_vs);
        Context.VSSetConstantBuffer(0, _params);
        Context.PSSetConstantBuffer(0, _params);
        Context.PSSetShader(_psScale);
        Context.PSSetShaderResource(0, surface.View);
        Context.PSSetSampler(0, filter == FilterMode.Nearest ? _nearest : _linear);
        Context.Draw(4, 0);
        Context.PSSetShaderResource(0, null!);
    }

    private void WriteParams(in ShaderParams p)
    {
        var mapped = Context.Map(_params, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe { *(ShaderParams*)mapped.DataPointer = p; } // blittable; StructureToPtr would box per frame
        Context.Unmap(_params, 0);
    }

    public void Dispose()
    {
        _lutView?.Dispose();
        _lutTexture?.Dispose();
        _linear.Dispose();
        _nearest.Dispose();
        _params.Dispose();
        _psScale.Dispose();
        _psConvert.Dispose();
        _vs.Dispose();
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>One card's planes and its pass-1 RGBA texture at source size, on a <see cref="RenderDevice"/>.</summary>
internal sealed class SourceSurface
{
    public PlaneTexture[] Planes { get; } = [new(), new(), new()];
    public ID3D11Texture2D? Texture { get; private set; }
    public ID3D11RenderTargetView? Target { get; private set; }
    public ID3D11ShaderResourceView? View { get; private set; }

    /// <summary>Size of the last converted frame (0 × 0 before the first).</summary>
    public (int Width, int Height) Size { get; private set; }

    public void Upload(RenderDevice gpu, in VideoFrame frame)
    {
        var (device, context) = (gpu.Device, gpu.Context);
        var (cw, ch) = frame.ChromaSize;
        switch (frame.Format)
        {
            case PixelFormat.Nv12:
                Planes[0].Upload(device, context, Format.R8_UNorm, frame.Width, frame.Height, frame.Plane0, frame.Stride0, frame.Width);
                Planes[1].Upload(device, context, Format.R8G8_UNorm, cw, ch, frame.Plane1, frame.Stride1, cw * 2);
                break;
            case PixelFormat.Yuy2:
                Planes[0].Upload(device, context, Format.R8G8B8A8_UNorm, cw, frame.Height, frame.Plane0, frame.Stride0, cw * 4);
                break;
            case PixelFormat.I420 or PixelFormat.I422 or PixelFormat.I444:
                Planes[0].Upload(device, context, Format.R8_UNorm, frame.Width, frame.Height, frame.Plane0, frame.Stride0, frame.Width);
                Planes[1].Upload(device, context, Format.R8_UNorm, cw, ch, frame.Plane1, frame.Stride1, cw);
                Planes[2].Upload(device, context, Format.R8_UNorm, cw, ch, frame.Plane2, frame.Stride2, cw);
                break;
            case PixelFormat.Bgra:
                Planes[0].Upload(device, context, Format.B8G8R8A8_UNorm, frame.Width, frame.Height, frame.Plane0, frame.Stride0, frame.Width * 4);
                break;
        }

        if (Texture == null || Size != (frame.Width, frame.Height))
        {
            ReleaseTexture();
            Texture = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)frame.Width, (uint)frame.Height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            Target = device.CreateRenderTargetView(Texture);
            View = device.CreateShaderResourceView(Texture);
            Size = (frame.Width, frame.Height);
        }
    }

    /// <summary>Forgets the picture (source lost); the planes stay for the next frame.</summary>
    public void ReleaseTexture()
    {
        View?.Dispose();
        Target?.Dispose();
        Texture?.Dispose();
        (View, Target, Texture) = (null, null, null);
        Size = default;
    }

    public void Release()
    {
        foreach (var p in Planes) p.Release();
        ReleaseTexture();
    }
}

/// <summary>A dynamic texture for one source plane, recreated when size or format changes.</summary>
internal sealed class PlaneTexture
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
