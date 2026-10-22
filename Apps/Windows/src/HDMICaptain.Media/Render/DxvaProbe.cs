using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HDMICaptain.Media.Render;

/// <summary>One adapter's hardware MJPEG decode support (D3D11 video decoder profiles).</summary>
public sealed record DxvaMjpegSupport(string Adapter, IReadOnlyList<string> Profiles, int ProfileCount, string? Error);

/// <summary>
/// Lists which D3D11 MJPEG decoder profiles (MJPEG_VLD_420/422/444/4444, d3d11.h in SDK 26100) each adapter
/// exposes, and which output formats they accept. Research only (task 090 step 3, windows.md §3): not used for decode.
/// </summary>
public static class DxvaProbe
{
    private static readonly (string Name, Guid Id)[] MjpegProfiles =
    [
        ("MJPEG_VLD_420", new Guid(0x725cb506, 0x0c29, 0x43c4, 0x94, 0x40, 0x8e, 0x93, 0x97, 0x90, 0x3a, 0x04)),
        ("MJPEG_VLD_422", new Guid(0x5b77b9cd, 0x1a35, 0x4c30, 0x9f, 0xd8, 0xef, 0x4b, 0x60, 0xc0, 0x35, 0xdd)),
        ("MJPEG_VLD_444", new Guid(0xd95161f9, 0x0d44, 0x47e6, 0xbc, 0xf5, 0x1b, 0xfb, 0xfb, 0x26, 0x8f, 0x97)),
        ("MJPEG_VLD_4444", new Guid(0xc91748d5, 0xfd18, 0x4aca, 0x9d, 0xb3, 0x3a, 0x66, 0x34, 0xab, 0x54, 0x7d)),
    ];

    private static readonly Format[] Outputs = [Format.NV12, Format.YUY2, Format.AYUV, Format.B8G8R8A8_UNorm, Format.R8G8B8A8_UNorm];

    public static IReadOnlyList<DxvaMjpegSupport> Run()
    {
        var result = new List<DxvaMjpegSupport>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
            using (adapter)
            {
                var desc = adapter.Description1;
                if ((desc.Flags & AdapterFlags.Software) != 0) continue;
                var name = desc.Description;
                FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
                var created = D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.VideoSupport,
                    levels, out ID3D11Device? device, out ID3D11DeviceContext? context);
                if (created.Failure || device == null || context == null)
                {
                    result.Add(new DxvaMjpegSupport(name, [], 0, $"D3D11CreateDevice 0x{created.Code:X8}"));
                    continue;
                }
                using (device)
                using (context)
                using (var video = device.QueryInterfaceOrNull<ID3D11VideoDevice>())
                {
                    if (video == null)
                    {
                        result.Add(new DxvaMjpegSupport(name, [], 0, "no ID3D11VideoDevice"));
                        continue;
                    }
                    var count = video.VideoDecoderProfileCount;
                    var found = new List<string>();
                    for (uint p = 0; p < count; p++)
                    {
                        if (video.GetVideoDecoderProfile(p, out var profile).Failure) continue;
                        foreach (var (profileName, id) in MjpegProfiles)
                        {
                            if (id != profile) continue;
                            var formats = Outputs.Where(f => video.CheckVideoDecoderFormat(profile, f, out var ok).Success && ok)
                                .Select(f => f.ToString());
                            found.Add($"{profileName} -> {string.Join(", ", formats)}");
                        }
                    }
                    result.Add(new DxvaMjpegSupport(name, found, (int)count, null));
                }
            }
        return result;
    }
}
