using System.Diagnostics;
using System.Runtime.InteropServices;
using HDMICaptain.Core;
using HDMICaptain.Media.Video;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Luid = Vortice.Luid;

namespace HDMICaptain.Media.Render;

/// <summary>
/// Multi-View grid (task 098): every card converted on one device and drawn into its <see cref="TileLayout"/> cell
/// of a single swap chain. Frames are converted on their sources' threads; a compose thread presents the latest
/// picture of every tile once the swap chain can take a frame, so two 60 fps cards never queue behind each other.
/// Tiles use the default picture (fit, auto range and matrix); per-card looks stay in the card windows.
/// </summary>
public sealed class GridRenderer : IDisposable
{
    private static readonly RenderOptions TileOptions = new();
    private static readonly (float, float, float, float, float) Identity = (0, 1, 1, 1, 0);

    private readonly object _gate = new();
    private readonly bool _warp;
    private readonly List<SourceSurface> _tiles = [];
    private readonly AutoResetEvent _dirty = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private RenderDevice _gpu;
    private SwapChainTarget? _target;
    private Luid? _adapterRequest;

    private GridRenderer(bool warp, Luid? adapter)
    {
        _warp = warp;
        _gpu = new RenderDevice(warp, adapter);
        _thread = new Thread(ComposeLoop) { IsBackground = true, Name = "Grid compose" };
        _thread.Start();
    }

    public static GridRenderer Create(nint hwnd) => new(false, RenderDevice.AdapterForWindow(hwnd));

    public static GridRenderer CreateWarp() => new(true, null);

    public string AdapterName => _gpu.AdapterName;

    /// <summary>Gap between cells, in pixels.</summary>
    public const int Gap = 2;

    /// <summary>Number of cells; new ones start black, removed ones release their textures.</summary>
    public int TileCount
    {
        get { lock (_gate) return _tiles.Count; }
        set
        {
            lock (_gate)
            {
                while (_tiles.Count < value) _tiles.Add(new SourceSurface());
                while (_tiles.Count > value)
                {
                    _tiles[^1].Release();
                    _tiles.RemoveAt(_tiles.Count - 1);
                }
            }
            _dirty.Set();
        }
    }

    /// <summary>The single composition swap chain (see <see cref="D3DRenderer.CreateCompositionTarget"/>).</summary>
    public SwapChainTarget CreateCompositionTarget(int width, int height, Action<IDXGISwapChain1> attach)
    {
        var target = new SwapChainTarget(0, attach, width, height);
        lock (_gate)
        {
            _target?.Dispose();
            _gpu.CreateSwapChain(target);
            _target = target;
        }
        _dirty.Set();
        return target;
    }

    /// <summary>Converts <paramref name="frame"/> into cell <paramref name="tile"/>; the compose thread presents it.</summary>
    public void Render(int tile, in VideoFrame frame)
    {
        lock (_gate)
        {
            if ((uint)tile >= (uint)_tiles.Count) return;
            _gpu.Convert(_tiles[tile], frame, TileOptions, Identity);
        }
        _dirty.Set();
    }

    /// <summary>Blacks out a cell whose card stopped, so a stale picture never lingers.</summary>
    public void ClearTile(int tile)
    {
        lock (_gate)
        {
            if ((uint)tile >= (uint)_tiles.Count) return;
            _tiles[tile].ReleaseTexture();
        }
        _dirty.Set();
    }

    /// <summary>Presents again (after a resize).</summary>
    public void Redraw() => _dirty.Set();

    /// <summary>Moves to the adapter driving <paramref name="hwnd"/>'s monitor, if it changed (on the compose thread).</summary>
    public void CheckAdapter(nint hwnd)
    {
        if (_warp) return;
        if (RenderDevice.AdapterForWindow(hwnd) is { } luid && !luid.Equals(_gpu.AdapterLuid))
        {
            lock (_gate) _adapterRequest = luid;
            _dirty.Set();
        }
    }

    /// <summary>Composes the grid at <paramref name="width"/> × <paramref name="height"/> into a texture and reads it back as RGBA8 (tests).</summary>
    public byte[] ReadComposedPixels(int width, int height)
    {
        lock (_gate)
        {
            var (device, context) = (_gpu.Device, _gpu.Context);
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.RenderTarget));
            using var view = device.CreateRenderTargetView(texture);
            context.OMSetRenderTargets(view);
            context.ClearRenderTargetView(view, new Vortice.Mathematics.Color4(0, 0, 0, 1));
            DrawCells(width, height);
            using var staging = device.CreateTexture2D(texture.Description with
            {
                Usage = ResourceUsage.Staging, BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
            });
            context.CopyResource(staging, texture);
            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[width * height * 4];
                for (var row = 0; row < height; row++)
                    Marshal.Copy(mapped.DataPointer + row * (int)mapped.RowPitch, pixels, row * width * 4, width * 4);
                return pixels;
            }
            finally
            {
                context.Unmap(staging, 0);
            }
        }
    }

    private void DrawCells(int width, int height)
    {
        var cells = TileLayout.Compute(_tiles.Count, width, height, Gap);
        for (var i = 0; i < cells.Length; i++)
            _gpu.DrawScaled(_tiles[i], cells[i], TileOptions.Scale, TileOptions.Filter);
    }

    private void ComposeLoop()
    {
        while (true)
        {
            _dirty.WaitOne();
            if (!_running) return;
            // Wait for the swap chain outside the lock, so sources can keep converting meanwhile.
            nint waitable;
            lock (_gate) waitable = _target is { IsVisible: true } t ? t.Waitable : 0;
            if (waitable != 0) PInvoke.WaitForSingleObject((HANDLE)waitable, 100);
            lock (_gate)
            {
                if (!_running) return;
                try
                {
                    if (_adapterRequest is { } luid)
                    {
                        _adapterRequest = null;
                        RecreateDevice(luid);
                    }
                    Compose();
                }
                catch (SharpGen.Runtime.SharpGenException e)
                {
                    Trace.WriteLine($"GridRenderer: {e.Message}");
                    RecreateDevice(_warp ? null : _gpu.AdapterLuid);
                }
            }
        }
    }

    private void Compose()
    {
        if (_target is not { IsVisible: true, SwapChain: not null } target) return;
        _gpu.PrepareTarget(target, waitForFrame: false);
        _gpu.BeginTarget(target);
        DrawCells(target.Width, target.Height);
        if (!_gpu.Present(target)) RecreateDevice(_warp ? null : _gpu.AdapterLuid);
    }

    private void RecreateDevice(Luid? adapter)
    {
        Trace.WriteLine("GridRenderer: recreating device");
        _target?.ReleaseDeviceObjects();
        foreach (var tile in _tiles) tile.Release();
        _gpu.Dispose();
        _gpu = new RenderDevice(_warp, adapter);
        if (_target != null) _gpu.CreateSwapChain(_target);
    }

    public void Dispose()
    {
        _running = false;
        _dirty.Set();
        _thread.Join();
        lock (_gate)
        {
            _target?.Dispose();
            _target = null;
            foreach (var tile in _tiles) tile.Release();
            _tiles.Clear();
            _gpu.Dispose();
        }
        _dirty.Dispose();
    }
}
