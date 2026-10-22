using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDMICaptain.Core;

/// <summary>
/// Source-generated JSON for the prefs, preset and profile models (trim/AOT safe). Keys are camelCase and
/// enums are camelCase strings, like the Swift Codable output (ScaleMode "fit", ChannelMode "leftToBoth").
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    Converters =
    [
        typeof(CamelCaseEnumConverter<ScaleMode>),
        typeof(CamelCaseEnumConverter<FilterMode>),
        typeof(CamelCaseEnumConverter<ColorRange>),
        typeof(CamelCaseEnumConverter<ChannelMode>),
        typeof(CamelCaseEnumConverter<ColorMatrix>),
        typeof(CamelCaseEnumConverter<GuideMask>),
    ])]
[JsonSerializable(typeof(DevicePreset))]
[JsonSerializable(typeof(ViewerPrefs))]
[JsonSerializable(typeof(List<Profile>))]
public sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>String enum with camelCase names; explicit [JsonStringEnumMemberName] values still win.</summary>
public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

/// <summary>
/// FormatPreference in the shape Swift's synthesized Codable writes for an enum with associated
/// values: <c>{"auto":{}}</c> or <c>{"pinned":{"width":1920,"height":1080,"frameRate":60}}</c>.
/// </summary>
public sealed class FormatPreferenceConverter : JsonConverter<FormatPreference>
{
    public override FormatPreference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var root = JsonElement.ParseValue(ref reader);
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("pinned", out var p) && p.ValueKind == JsonValueKind.Object
                && p.TryGetProperty("width", out var w) && w.TryGetInt32(out var width)
                && p.TryGetProperty("height", out var h) && h.TryGetInt32(out var height))
            {
                double? rate = p.TryGetProperty("frameRate", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetDouble() : null;
                return new FormatPreference.Pinned(width, height, rate);
            }
            if (root.TryGetProperty("auto", out _)) return new FormatPreference.Auto();
        }
        throw new JsonException("Unrecognised format preference.");
    }

    public override void Write(Utf8JsonWriter writer, FormatPreference value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case FormatPreference.Pinned p:
                writer.WriteStartObject("pinned");
                writer.WriteNumber("width", p.Width);
                writer.WriteNumber("height", p.Height);
                if (p.FrameRate is { } rate) writer.WriteNumber("frameRate", rate);
                writer.WriteEndObject();
                break;
            default:
                writer.WriteStartObject("auto");
                writer.WriteEndObject();
                break;
        }
        writer.WriteEndObject();
    }
}
