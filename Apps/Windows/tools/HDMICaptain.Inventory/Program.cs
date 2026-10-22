using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HDMICaptain.Core;
using HDMICaptain.Media.Capture;
using HDMICaptain.Media.Video;

// HDMICaptain.Inventory [--match <name>] [--out <file.json>] [--probe <seconds> [--pin WxH@fps] [--ignore-link] [--own-mjpeg]]
//                       [--bench-mjpeg <frames> [--pin WxH@fps]] [--attributes] | --probe-dxva
// Lists MF capture devices and their native types in the Mac device-inventory JSON shape (plus a "windows"
// block), and with --probe streams the auto-chosen type to report fps, drops and any HRESULT (task 089).
// --own-mjpeg captures raw MJPG and decodes it with libjpeg-turbo instead of Windows' NV12 twin (task 090).
// --bench-mjpeg captures that many raw MJPG frames, then decodes them offline on 1 and 2 threads (task 090).
// --probe-dxva lists each GPU's D3D11 MJPEG decoder profiles (task 090 step 3).

const string Usage = "usage: HDMICaptain.Inventory [--match <name>] [--out <file.json>] [--probe <seconds> [--pin WxH@fps] [--ignore-link] [--own-mjpeg]] [--bench-mjpeg <frames>] [--attributes] | --probe-dxva";
string? match = null, outPath = null;
var probeSeconds = 0;
var benchFrames = 0;
var dumpAttributes = false;
var ignoreLink = false;
var ownMjpeg = false;
FormatPreference? pin = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--match": match = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--attributes": dumpAttributes = true; break;
        case "--ignore-link": ignoreLink = true; break;
        case "--own-mjpeg": ownMjpeg = true; break;
        case "--probe-dxva":
            foreach (var a in HDMICaptain.Media.Render.DxvaProbe.Run())
                Console.WriteLine($"{a.Adapter}: {a.ProfileCount} decoder profiles; MJPEG: " +
                                  (a.Error ?? (a.Profiles.Count == 0 ? "none" : string.Join("; ", a.Profiles))));
            return 0;
        case "--bench-mjpeg": benchFrames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--pin":
            var parts = args[++i].Split('x', '@');
            pin = new FormatPreference.Pinned(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : null);
            break;
        case "--probe": probeSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

bool Matches(string name) => match == null || name.Contains(match, StringComparison.OrdinalIgnoreCase);

var video = new JsonArray();
var probes = new List<VideoDevice>();
foreach (var device in MfDevices.Video().Where(d => Matches(d.Name)))
{
    IReadOnlyList<NativeVideoType> types;
    JsonNode? error = null;
    try
    {
        types = MfDevices.NativeTypes(device.SymbolicLink);
    }
    catch (System.Runtime.InteropServices.COMException e)
    {
        types = [];
        error = MfErrors.Name(e.HResult);
    }
    var descriptors = MfDevices.Descriptors(types);
    var choice = MfDevices.Choose(types, null, device.Usb?.Link);
    var usb = device.Usb;
    video.Add(new JsonObject
    {
        ["autoChoice"] = choice is null ? null : $"{choice.Width}x{choice.Height} {choice.FourCC} @ {Num(choice.FrameRate)}",
        ["device"] = new JsonObject
        {
            ["id"] = device.SymbolicLink,
            ["kind"] = usb?.VendorId is null ? "builtIn" : "external",
            ["modelID"] = usb?.VendorId is { } v ? $"{device.Name}:{v:X4}:{usb.ProductId:X4}" : device.Name,
            ["name"] = device.Name,
            ["transport"] = usb?.VendorId is null ? "unknown" : "usb",
        },
        ["formats"] = new JsonArray([.. descriptors.Select(f => (JsonNode)new JsonObject
        {
            ["fourCC"] = f.FourCC,
            ["frameRateRanges"] = new JsonArray([.. f.FrameRateRanges.Select(r => (JsonNode)new JsonObject { ["max"] = r.Max, ["min"] = r.Min })]),
            ["height"] = f.Height,
            ["width"] = f.Width,
        })]),
        ["windows"] = new JsonObject
        {
            ["audioEndpoint"] = usb?.ContainerId is { } container ? HDMICaptain.Media.Audio.AudioEndpoints.CaptureFor(container)?.Name : null,
            ["busReportedName"] = usb?.BusReportedName,
            ["containerId"] = usb?.ContainerId?.ToString("B"),
            ["error"] = error,
            ["instanceId"] = usb?.InstanceId,
            ["linkSpeed"] = usb?.LinkSpeed,
            ["nativeTypes"] = new JsonArray([.. types.Select(t => (JsonNode)JsonValue.Create(
                $"#{t.Index} {t.FourCC} {t.Width}x{t.Height} {t.RateNumerator}/{t.RateDenominator} range={Tri(t.FullRange)} matrix={t.Matrix}{(t.DecodedByWindows ? " decodedByWindows" : "")}"))]),
            ["serial"] = usb?.Serial,
            ["usbDeviceInstanceId"] = usb?.UsbDeviceInstanceId,
            ["vidPid"] = usb?.VendorId is { } vid ? $"{vid:X4}:{usb.ProductId:X4}" : null,
        },
    });
    if (error == null) probes.Add(device);
    if (dumpAttributes && error == null)
    {
        var all = MfDevices.NativeTypeAttributes(device.SymbolicLink);
        for (var t = 0; t < all.Count; t++)
            Console.Error.WriteLine($"#{t}\n  " + string.Join("\n  ", all[t]));
    }
}

var audio = new JsonArray([.. HDMICaptain.Media.Audio.AudioEndpoints.Captures().Where(a => Matches(a.Name)).Select(a => (JsonNode)new JsonObject
{
    ["containerId"] = a.ContainerId?.ToString("B"),
    ["id"] = a.Id,
    ["kind"] = "endpoint",
    ["name"] = a.Name,
    ["transport"] = "wasapi",
})]);

var json = new JsonObject { ["audio"] = audio, ["video"] = video }
    .ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
if (outPath != null)
{
    File.WriteAllText(outPath, json + "\n");
    Console.Error.WriteLine($"wrote {outPath}");
}
else
{
    Console.WriteLine(json);
}

foreach (var device in probeSeconds > 0 ? probes : [])
{
    Console.Error.WriteLine($"probe {device.Name} for {probeSeconds} s");
    using var source = new MfSource(device.SymbolicLink, pin, ignoreLink ? null : device.Usb?.Link,
        ownMjpeg ? MjpegPolicy.PreferOwnDecode : MjpegPolicy.Auto);
    source.Log += line => Console.Error.WriteLine($"  {line}");
    var failed = new TaskCompletionSource<CaptureError>();
    source.Failed += e => failed.TrySetResult(e);
    var clock = Stopwatch.StartNew();
    try
    {
        source.Start();
    }
    catch (System.Runtime.InteropServices.COMException e)
    {
        Console.Error.WriteLine($"  open failed: {MfErrors.Name(e.HResult)}");
        continue;
    }
    for (var s = 0; s < probeSeconds && !failed.Task.IsCompleted; s++)
    {
        failed.Task.Wait(1000);
        Console.Error.WriteLine($"  t={clock.Elapsed.TotalSeconds:F0}s frames={source.Stats.Frames} dropped={source.Stats.Dropped}");
    }
    var seconds = clock.Elapsed.TotalSeconds;
    source.Stop();
    Console.Error.WriteLine($"  {source.Stats.Frames / seconds:F2} fps, {source.Stats.Dropped} dropped over {seconds:F1} s" +
                            (failed.Task.IsCompleted ? $", stopped by {failed.Task.Result}" : ""));
}

foreach (var device in benchFrames > 0 ? probes : [])
    BenchMjpeg(device, benchFrames, pin ?? new FormatPreference.Pinned(1920, 1080, 60));
return 0;

static void BenchMjpeg(VideoDevice device, int count, FormatPreference pin)
{
    if (!MjpegDecoder.IsAvailable)
    {
        Console.Error.WriteLine("bench: turbojpeg.dll not found (make win-deps)");
        return;
    }
    Console.Error.WriteLine($"bench {device.Name}: capturing {count} raw MJPG frames");
    var frames = new List<byte[]>(count);
    var done = new ManualResetEventSlim();
    using (var source = new MfSource(device.SymbolicLink, pin, null, MjpegPolicy.PreferOwnDecode))
    {
        source.Log += line => Console.Error.WriteLine($"  {line}");
        source.CompressedFrameArrived += (data, _) =>
        {
            if (frames.Count < count) frames.Add(data.ToArray());
            if (frames.Count >= count) done.Set();
        };
        try
        {
            source.Start();
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            Console.Error.WriteLine($"  open failed: {MfErrors.Name(e.HResult)}");
            return;
        }
        if (source.CurrentType?.FourCC != "MJPG")
        {
            Console.Error.WriteLine($"  no MJPG type at {pin}; got {source.CurrentType?.FourCC}");
            return;
        }
        var failed = new ManualResetEventSlim();
        source.Failed += _ => failed.Set();
        WaitHandle.WaitAny([done.WaitHandle, failed.WaitHandle], TimeSpan.FromSeconds(count / 10.0 + 10));
        source.Stop();
    }
    if (frames.Count == 0)
    {
        // Another app streaming the card's decoded NV12 twin blocks the raw MJPG pin (MF_E_HW_MFT_FAILED_START_STREAMING).
        // The shared NV12 stream still works: re-encode its frames at the card's typical 4:2:2 so the decode cost is realistic.
        Console.Error.WriteLine("  raw MJPG unavailable; re-encoding frames from the shared stream as 4:2:2 JPEG (quality 85)");
        frames = ReencodedFrames(device, count, pin);
        if (frames.Count == 0)
        {
            Console.Error.WriteLine("  no frames");
            return;
        }
    }
    // Cards send a few non-JPEG frames at stream start; they are dropped live too, so leave them out.
    using (var probe = new MjpegDecoder())
    {
        var total = frames.Count;
        frames = [.. frames.Where(f => probe.TryDecode(f, 0, out _))];
        if (frames.Count == 0)
        {
            Console.Error.WriteLine($"  none of {total} frames decoded: {probe.LastError}");
            return;
        }
        probe.TryDecode(frames[0], 0, out var first);
        Console.Error.WriteLine($"  {frames.Count} of {total} frames decode, {first.Width}x{first.Height} -> {first.Format}, " +
                                $"mean {frames.Average(f => f.Length) / 1024:F0} KiB per frame");
    }
    DecodeTimes("captured", frames);

    // Worst case for the entropy decoder: per-pixel noise at the same size (≈ an MJPEG card's maximum bitrate).
    using (var probe = new MjpegDecoder())
    {
        probe.TryDecode(frames[0], 0, out var first);
        var noise = new byte[first.Width * first.Height * 4];
        new Random(1).NextBytes(noise);
        var jpeg = MjpegDecoder.EncodeBgra(noise, first.Width, first.Height, first.Width * 4, first.Format, 85);
        Console.Error.WriteLine($"  noise fixture {jpeg.Length / 1024} KiB");
        DecodeTimes("noise", [.. Enumerable.Repeat(jpeg, 120)]);
    }
}

static void DecodeTimes(string label, List<byte[]> frames)
{
    for (var threads = 1; threads <= 2; threads++)
    {
        var times = new double[frames.Count];
        var failures = 0;
        var wall = Stopwatch.StartNew();
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            using var decoder = new MjpegDecoder();
            decoder.TryDecode(frames[0], 0, out _); // warm-up: allocate planes
            for (var f = t; f < frames.Count; f += threads)
            {
                if (!decoder.TryDecode(frames[f], f, out _)) Interlocked.Increment(ref failures);
                times[f] = decoder.LastDecodeMilliseconds;
            }
        });
        var seconds = wall.Elapsed.TotalSeconds;
        Array.Sort(times);
        double Pct(double p) => times[Math.Min(times.Length - 1, (int)Math.Ceiling(p * times.Length) - 1)];
        Console.Error.WriteLine($"  {label}, {threads} thread(s): mean {times.Average():F2} ms, p50 {Pct(0.50):F2} ms, p95 {Pct(0.95):F2} ms, " +
                                $"max {times[^1]:F2} ms per frame; {frames.Count / seconds:F0} frames/s; {failures} failed");
    }
}

static unsafe List<byte[]> ReencodedFrames(VideoDevice device, int count, FormatPreference pin)
{
    var raw = new List<(byte[] Data, int Width, int Height, int Stride)>();
    var done = new ManualResetEventSlim();
    using (var source = new MfSource(device.SymbolicLink, pin, null))
    {
        source.FrameArrived += (in VideoFrame f) =>
        {
            if (f.Format != PixelFormat.Nv12 || raw.Count >= count) { done.Set(); return; }
            // NV12: luma rows then interleaved CbCr rows at the same stride.
            var bytes = new ReadOnlySpan<byte>((void*)f.Plane0, f.Stride0 * f.Height).ToArray();
            var chroma = new ReadOnlySpan<byte>((void*)f.Plane1, f.Stride1 * f.Height / 2).ToArray();
            raw.Add(([.. bytes, .. chroma], f.Width, f.Height, f.Stride0));
            if (raw.Count >= count) done.Set();
        };
        try
        {
            source.Start();
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            Console.Error.WriteLine($"  open failed: {MfErrors.Name(e.HResult)}");
            return [];
        }
        done.Wait(TimeSpan.FromSeconds(count / 10.0 + 10));
        source.Stop();
    }
    var result = new List<byte[]>(raw.Count);
    foreach (var (data, w, h, stride) in raw)
    {
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // Full-range BT.601 (what the card's JPEG-derived NV12 reports).
                double l = data[y * stride + x];
                var c = h * stride + y / 2 * stride + (x & ~1);
                double cb = data[c] - 128.0, cr = data[c + 1] - 128.0;
                var i = (y * w + x) * 4;
                bgra[i] = Clamp(l + 1.772 * cb);
                bgra[i + 1] = Clamp(l - 0.344136 * cb - 0.714136 * cr);
                bgra[i + 2] = Clamp(l + 1.402 * cr);
                bgra[i + 3] = 255;
            }
        result.Add(MjpegDecoder.EncodeBgra(bgra, w, h, w * 4, PixelFormat.I422, 85));
    }
    return result;

    static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}

static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
static string Tri(bool? value) => value switch { true => "full", false => "limited", null => "unset" };
