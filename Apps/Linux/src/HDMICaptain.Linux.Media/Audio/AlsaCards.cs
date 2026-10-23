using HDMICaptain.Linux.Media.Capture;

namespace HDMICaptain.Linux.Media.Audio;

/// <summary>An ALSA sound card and the USB device it belongs to (sysfs), for pairing a card's audio with its video.</summary>
public sealed record AlsaCard(int Index, string Id, string Name, UsbIdentity? Usb)
{
    /// <summary>ALSA hardware capture device name (the first PCM of the card).</summary>
    public string HwName => $"hw:{Index},0";
}

public static class AlsaCards
{
    public const string SysClass = "/sys/class/sound";

    public static IReadOnlyList<AlsaCard> List(string sysClass = SysClass)
    {
        var cards = new List<AlsaCard>();
        if (!Directory.Exists(sysClass)) return cards;
        foreach (var dir in Directory.GetDirectories(sysClass, "card*"))
        {
            var node = Path.GetFileName(dir);
            if (!int.TryParse(node.AsSpan(4), out var index)) continue;
            var id = Read(Path.Combine(dir, "id")) ?? node;
            var name = LongName(index) ?? id;
            cards.Add(new AlsaCard(index, id, name, V4l2Devices.ReadUsb(node, sysClass)));
        }
        return cards.OrderBy(c => c.Index).ToList();
    }

    /// <summary>The card on the same USB device as a video node (same sysfs USB device path).</summary>
    public static AlsaCard? PairedWith(UsbIdentity video, IReadOnlyList<AlsaCard> cards) =>
        cards.FirstOrDefault(c => c.Usb?.SysPath == video.SysPath);

    // /proc/asound/cards: " 1 [U25854         ]: USB-Audio - UGREEN-25854"
    private static string? LongName(int index)
    {
        if (!File.Exists("/proc/asound/cards")) return null;
        foreach (var line in File.ReadLines("/proc/asound/cards"))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith($"{index} [", StringComparison.Ordinal)) continue;
            var dash = trimmed.IndexOf(" - ", StringComparison.Ordinal);
            return dash > 0 ? trimmed[(dash + 3)..].Trim() : null;
        }
        return null;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
