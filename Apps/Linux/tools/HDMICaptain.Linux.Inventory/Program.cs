using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HDMICaptain.Core;
using HDMICaptain.Linux.Media.Audio;
using HDMICaptain.Linux.Media.Capture;
using HDMICaptain.Linux.Media.Render;
using HDMICaptain.Linux.Media.Video;

// hdmi-captain-inventory [--match <name>] [--out <file.json>] [--probe <seconds> [--pin WxH@fps] [--format MJPG|YUY2|NV12]]
//                        [--save <frames> <dir>] [--bench-mjpeg <dir>]
// Lists V4L2 capture nodes and their native types in the Mac device-inventory JSON shape (plus a "linux" object),
// and the ALSA cards. --probe streams the auto choice (or the pinned one) for N seconds through the real frame path
// (copy/decode into the renderer's slots, no window) and prints fps, drops and decode times (task 108).
const string Usage = "usage: hdmi-captain-inventory [--match <name>] [--out <file.json>] [--probe <seconds> [--pin WxH@fps] [--format F]] [--save <frames> <dir>] [--bench-mjpeg <dir>]";

string? match = null, outPath = null, saveDir = null, benchDir = null, formatFilter = null;
int probeSeconds = 0, saveFrames = 0;
FormatPreference? pin = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--match": match = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--probe": probeSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--format": formatFilter = MediaSubtypes.Canonicalize(args[++i]); break;
        case "--pin":
            var p = args[++i].Split('x', '@');
            pin = new FormatPreference.Pinned(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture),
                p.Length > 2 ? double.Parse(p[2], CultureInfo.InvariantCulture) : null);
            break;
        case "--save": saveFrames = int.Parse(args[++i], CultureInfo.InvariantCulture); saveDir = args[++i]; break;
        case "--bench-mjpeg": benchDir = args[++i]; break;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}

if (benchDir != null) return BenchMjpeg(benchDir);

static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

var devices = V4l2Devices.List(Console.Error.WriteLine)
    .Where(d => match == null || d.Name.Contains(match, StringComparison.OrdinalIgnoreCase) || (d.Usb?.Product?.Contains(match, StringComparison.OrdinalIgnoreCase) ?? false))
    .ToList();
var cards = AlsaCards.List();

FormatChoice? Choose(V4l2Device d)
{
    var formats = formatFilter == null ? d.Descriptors : d.Descriptors.Select(f => f.FourCC == formatFilter ? f : f with { FrameRateRanges = [] }).ToList();
    return FormatSelector.Select(formats, pin, d.Usb?.Link);
}

var video = new JsonArray();
foreach (var d in devices)
{
    var choice = Choose(d);
    var usb = d.Usb;
    video.Add(new JsonObject
    {
        ["autoChoice"] = choice is null ? null : $"{choice.Format.Width}x{choice.Format.Height} {choice.Format.FourCC} @ {Num(choice.FrameRate)}",
        ["device"] = new JsonObject
        {
            ["id"] = d.BusInfo,
            ["kind"] = usb is null ? "builtIn" : "external",
            ["modelID"] = usb is null ? d.Name : $"{d.Name}:{usb.VendorId:X4}:{usb.ProductId:X4}",
            ["name"] = d.Name,
            ["transport"] = usb is null ? "unknown" : "usb",
        },
        ["formats"] = new JsonArray([.. d.Descriptors.Select(f => (JsonNode)new JsonObject
        {
            ["fourCC"] = f.FourCC,
            ["frameRateRanges"] = new JsonArray([.. f.FrameRateRanges.Select(r => (JsonNode)new JsonObject { ["max"] = r.Max, ["min"] = r.Min })]),
            ["height"] = f.Height,
            ["width"] = f.Width,
        })]),
        ["linux"] = new JsonObject
        {
            ["alsaCard"] = usb is null ? null : AlsaCards.PairedWith(usb, cards)?.HwName,
            ["busInfo"] = d.BusInfo,
            ["driver"] = d.Driver,
            ["key"] = usb?.Key,
            ["manufacturer"] = usb?.Manufacturer,
            ["node"] = d.Path,
            ["serial"] = usb?.Serial,
            ["usbProduct"] = usb?.Product,
            ["usbSpeedMbps"] = usb?.SpeedMbps,
            ["v4l2Types"] = new JsonArray([.. d.Formats.Select(f => (JsonNode)JsonValue.Create(
                $"{f.V4l2FourCC}{(f.Compressed ? " (compressed)" : "")} {f.Width}x{f.Height} {string.Join(",", f.Intervals.Select(i => $"{i.Num}/{i.Den}"))}"))]),
            ["vidPid"] = usb is null ? null : $"{usb.VendorId:X4}:{usb.ProductId:X4}",
        },
    });
}

var audio = new JsonArray([.. cards.Select(c => (JsonNode)new JsonObject
{
    ["id"] = c.HwName,
    ["kind"] = "alsa",
    ["name"] = c.Name,
    ["transport"] = c.Usb is null ? "unknown" : "usb",
    ["vidPid"] = c.Usb is null ? null : $"{c.Usb.VendorId:X4}:{c.Usb.ProductId:X4}",
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

var status = 0;
foreach (var d in probeSeconds > 0 || saveFrames > 0 ? devices : [])
{
    var choice = Choose(d);
    if (choice == null)
    {
        Console.Error.WriteLine($"{d.Path}: no usable format");
        status = 1;
        continue;
    }
    status |= Probe(d, d.Formats.First(f => f.Descriptor == choice.Format), choice.FrameRate, Math.Max(probeSeconds, 10));
}
return status;

int Probe(V4l2Device device, V4l2Format format, double fps, int seconds)
{
    using var renderer = new GlesRenderer();
    using var writer = new FrameWriter(renderer);
    var decode = new List<double>(seconds * 70);
    var timestamps = new List<long>(seconds * 70);
    var saved = 0;
    if (saveDir != null) Directory.CreateDirectory(saveDir);
    var stopped = 0;
    unsafe
    {
        using var source = new V4l2Source(device.Path, (data, length, stream, ts) =>
        {
            if (saved < saveFrames && saveDir != null)
                File.WriteAllBytes(Path.Combine(saveDir, $"frame{saved++:D4}.{(stream.FourCC == "MJPG" ? "jpg" : stream.FourCC.ToLowerInvariant())}"),
                    new ReadOnlySpan<byte>(data, length).ToArray());
            writer.Write(stream.FourCC, data, length, stream.Width, stream.Height, stream.BytesPerLine, ts);
            decode.Add(writer.LastMilliseconds);
            timestamps.Add(ts);
        });
        source.Stopped += e => stopped = e;
        var r = source.Start(format, fps);
        if (r != 0)
        {
            Console.Error.WriteLine($"{device.Path}: start {format.V4l2FourCC} {format.Width}x{format.Height}@{Num(fps)} failed: {Errno.Name(r)}");
            return 1;
        }
        var s = source.Stream!;
        Console.Error.WriteLine($"{device.Path}: streaming {s.FourCC} {s.Width}x{s.Height} @ {Num(s.FrameRate)} ({s.Buffers} buffers, pitch {s.BytesPerLine}) for {seconds} s");
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds && stopped == 0) Thread.Sleep(100);
        var c = source.Counters;
        source.Dispose();
        var span = timestamps.Count > 1 ? (timestamps[^1] - timestamps[0]) / 1e9 : 0;
        var intervals = timestamps.Zip(timestamps.Skip(1), (a, b) => (b - a) / 1e6).ToList();
        var sorted = decode.Order().ToList();
        Console.Error.WriteLine(
            $"  frames {c.Frames}, fps {(span > 0 ? Num((timestamps.Count - 1) / span) : "-")}, skipped (newest wins) {c.Skipped}, sequence gaps {c.SequenceGaps}, " +
            $"empty/error buffers {c.Empty}, rejected {writer.Rejected} ({writer.LastError ?? "none"}), jpeg warnings {writer.JpegWarnings}");
        if (intervals.Count > 0)
            Console.Error.WriteLine($"  interval ms: mean {Num(intervals.Average())}, max {Num(intervals.Max())}, > 1.5× nominal {intervals.Count(i => i > 1500 / fps)}");
        if (sorted.Count > 0)
            Console.Error.WriteLine($"  copy/decode ms: median {Num(sorted[sorted.Count / 2])}, p99 {Num(sorted[(int)(sorted.Count * 0.99)])}, max {Num(sorted[^1])}");
        if (stopped != 0) Console.Error.WriteLine($"  stream stopped: {Errno.Name(stopped)}");
        return stopped != 0 ? 1 : 0;
    }
}

static int BenchMjpeg(string dir)
{
    var files = Directory.GetFiles(dir, "*.jpg").Order().Select(File.ReadAllBytes).ToList();
    if (files.Count == 0)
    {
        Console.Error.WriteLine($"no .jpg in {dir}");
        return 1;
    }
    using var renderer = new GlesRenderer();
    using var writer = new FrameWriter(renderer);
    var times = new List<double>();
    for (var round = 0; round < 5; round++)
        foreach (var jpeg in files)
        {
            unsafe
            {
                fixed (byte* p = jpeg) writer.Write("MJPG", p, jpeg.Length, 0, 0, 0, 1);
            }
            if (round > 0) times.Add(writer.LastMilliseconds);  // round 0 warms up
        }
    times.Sort();
    Console.Error.WriteLine($"{files.Count} frames × 4: median {Num(times[times.Count / 2])} ms, p99 {Num(times[(int)(times.Count * 0.99)])} ms, max {Num(times[^1])} ms, rejected {writer.Rejected}");
    return 0;
}
