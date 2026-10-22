using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace HDMICaptain.Media.Tests;

/// <summary>Proves both interop stacks load: Vortice (D3D11 on WARP) and CsWin32 (Media Foundation).</summary>
public class SmokeTests
{
    [Fact]
    public void CreatesWarpDevice()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out ID3D11Device? device).CheckError();
        using (device)
            Assert.True(device!.FeatureLevel >= FeatureLevel.Level_11_0);
    }

    [Fact]
    public void StartsAndStopsMediaFoundation()
    {
        MediaFoundation.Startup();
        MediaFoundation.Shutdown();
    }
}
