using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using HDMICaptain.Core;
using HDMICaptain.Media.Capture;

// HDMICaptain.Inventory [--match <name>] [--out <file.json>] [--probe <seconds> [--pin WxH@fps] [--ignore-link]] [--attributes]
// Lists MF capture devices and their native types in the Mac device-inventory JSON shape (plus a "windows"
// block), and with --probe streams the auto-chosen type to report fps, drops and any HRESULT (task 089).

string? match = null, outPath = null;
var probeSeconds = 0;
var dumpAttributes = false;
var ignoreLink = false;
FormatPreference? pin = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--match": match = args[++i]; break;
        case "--out": outPath = args[++i]; break;
        case "--attributes": dumpAttributes = true; break;
        case "--ignore-link": ignoreLink = true; break;
        case "--pin":
            var parts = args[++i].Split('x', '@');
            pin = new FormatPreference.Pinned(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture),
                parts.Length > 2 ? double.Parse(parts[2], CultureInfo.InvariantCulture) : null);
            break;
        case "--probe": probeSeconds = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default:
            Console.Error.WriteLine("usage: HDMICaptain.Inventory [--match <name>] [--out <file.json>] [--probe <seconds> [--pin WxH@fps] [--ignore-link]] [--attributes]");
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

var audio = new JsonArray([.. MfDevices.Audio().Where(a => Matches(a.Name)).Select(a => (JsonNode)new JsonObject
{
    ["id"] = a.EndpointId,
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
    using var source = new MfSource(device.SymbolicLink, pin, ignoreLink ? null : device.Usb?.Link);
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
return 0;

static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
static string Tri(bool? value) => value switch { true => "full", false => "limited", null => "unset" };
