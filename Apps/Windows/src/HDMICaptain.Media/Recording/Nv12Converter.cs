using System.Collections.Concurrent;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HDMICaptain.Media.Recording;

/// <summary>One encoder input: an NV12 texture owned by the recorder, plus the frame's time.</summary>
internal sealed class Nv12Slot : IDisposable
{
    public required ID3D11Texture2D Texture { get; init; }
    public required ID3D11RenderTargetView Luma { get; init; }
    public required ID3D11RenderTargetView Chroma { get; init; }
    public ID3D11Texture2D? Staging { get; set; }
    public long Time { get; set; }

    public void Dispose()
    {
        Staging?.Dispose();
        Chroma.Dispose();
        Luma.Dispose();
        Texture.Dispose();
    }
}

/// <summary>
/// Converts the renderer's pass-1 RGBA texture to BT.709 limited-range NV12 on the GPU (two draws into the
/// planes of a pooled NV12 texture), on the render thread inside the render lock. The pool decouples the
/// renderer, which reuses its texture every frame, from the encoder thread.
/// </summary>
internal sealed class Nv12Converter : IDisposable
{
    private const string Hlsl = """
        Texture2D<float4> Src : register(t0);
        SamplerState Linear : register(s0);
        struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
        V VSMain(uint id : SV_VertexID)
        {
            V o;
            float2 p = float2((id << 1) & 2, id & 2);
            o.uv = p;
            o.pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }
        float PSLuma(V i) : SV_Target
        {
            float3 c = Src.Sample(Linear, i.uv).rgb;
            return (16.0 + 219.0 * dot(c, float3(0.2126, 0.7152, 0.0722))) / 255.0;
        }
        float2 PSChroma(V i) : SV_Target
        {
            // Bilinear at the chroma texel centre averages the 2x2 source pixels it covers.
            float3 c = Src.Sample(Linear, i.uv).rgb;
            float cb = dot(c, float3(-0.1146, -0.3854, 0.5));
            float cr = dot(c, float3(0.5, -0.4542, -0.0458));
            return (128.0 + 224.0 * float2(cb, cr)) / 255.0;
        }
        """;

    private static readonly Lazy<(byte[] Vs, byte[] Luma, byte[] Chroma)> Bytecode = new(() => (
        Compile("VSMain", "vs_5_0"), Compile("PSLuma", "ps_5_0"), Compile("PSChroma", "ps_5_0")));

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _psLuma;
    private readonly ID3D11PixelShader _psChroma;
    private readonly ID3D11SamplerState _linear;
    private readonly Nv12Slot[] _slots;
    private readonly ConcurrentQueue<Nv12Slot> _free = new();
    private readonly SemaphoreSlim _freeCount;
    private ID3D11ShaderResourceView? _sourceView;
    private nint _sourcePointer;

    public Nv12Converter(ID3D11Device device, int width, int height, int poolSize)
    {
        Device = device;
        Context = device.ImmediateContext;
        Width = width;
        Height = height;
        // The encoder thread (MFT through the DXGI device manager, or staging readback) shares this device.
        using (var multithread = device.QueryInterface<ID3D11Multithread>())
            multithread.SetMultithreadProtected(true);

        var (vs, luma, chroma) = Bytecode.Value;
        _vs = device.CreateVertexShader(vs);
        _psLuma = device.CreatePixelShader(luma);
        _psChroma = device.CreatePixelShader(chroma);
        _linear = device.CreateSamplerState(SamplerDescription.LinearClamp);
        _slots = new Nv12Slot[poolSize];
        for (var i = 0; i < poolSize; i++)
        {
            var texture = device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height, 1, 1,
                BindFlags.RenderTarget | BindFlags.ShaderResource));
            _slots[i] = new Nv12Slot
            {
                Texture = texture,
                Luma = device.CreateRenderTargetView(texture, new RenderTargetViewDescription(texture, RenderTargetViewDimension.Texture2D, Format.R8_UNorm)),
                Chroma = device.CreateRenderTargetView(texture, new RenderTargetViewDescription(texture, RenderTargetViewDimension.Texture2D, Format.R8G8_UNorm)),
            };
            _free.Enqueue(_slots[i]);
        }
        _freeCount = new SemaphoreSlim(poolSize, poolSize);
    }

    /// <summary>The renderer's device (this converter holds a reference) and its immediate context.</summary>
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>Converts <paramref name="source"/> into a free slot; null when none is free and <paramref name="wait"/> is false.</summary>
    public Nv12Slot? Convert(ID3D11Texture2D source, long time, bool wait)
    {
        if (!_freeCount.Wait(wait ? Timeout.Infinite : 0)) return null;
        _free.TryDequeue(out var slot);
        slot!.Time = time;

        if (_sourcePointer != source.NativePointer)
        {
            _sourceView?.Dispose(); // the view kept the previous texture alive, so its address can't be reused meanwhile
            _sourceView = Device.CreateShaderResourceView(source);
            _sourcePointer = source.NativePointer;
        }
        var context = Context;
        // Output first: the renderer may leave its source bound as a render target, and D3D11 silently
        // drops an SRV of a resource that is still bound for output.
        context.OMSetRenderTargets(slot.Luma);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vs);
        context.PSSetShaderResource(0, _sourceView!);
        context.PSSetSampler(0, _linear);

        context.RSSetViewport(0, 0, Width, Height);
        context.PSSetShader(_psLuma);
        context.Draw(3, 0);

        context.OMSetRenderTargets(slot.Chroma);
        context.RSSetViewport(0, 0, Width / 2, Height / 2);
        context.PSSetShader(_psChroma);
        context.Draw(3, 0);

        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        context.Flush();
        return slot;
    }

    public void Release(Nv12Slot slot)
    {
        _free.Enqueue(slot);
        _freeCount.Release();
    }

    public void Dispose()
    {
        foreach (var slot in _slots) slot.Dispose();
        _sourceView?.Dispose();
        _linear.Dispose();
        _psChroma.Dispose();
        _psLuma.Dispose();
        _vs.Dispose();
        Context.Dispose();
        Device.Dispose();
        _freeCount.Dispose();
    }

    private static byte[] Compile(string entry, string profile) =>
        Compiler.Compile(Hlsl, entry, "Nv12Converter.hlsl", profile, ShaderFlags.OptimizationLevel3, EffectFlags.None).ToArray();
}
