using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HDMICaptain.Core;
using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.App;

/// <summary>A genuine licence key's contents (services/licence/src/keys.js).</summary>
public sealed record LicenceKeyInfo(string Id, int Product, DateOnly Issued);

/// <summary>
/// Licence keys sold through Stripe (task 119): "HC1-" + base64url(16-byte payload ‖ ECDSA P-256 signature). The app
/// checks the signature with the service's public key, so typos and forged keys fail offline before any request.
/// </summary>
public static class LicenceKey
{
    public const string Prefix = "HC1-";
    private const int PayloadBytes = 16, SignatureBytes = 64;
    private static readonly DateOnly Epoch = new(2026, 1, 1);

    /// <summary>The key's contents when it is signed by <paramref name="publicKeySpki"/> (base64 SPKI), else null.</summary>
    public static LicenceKeyInfo? Verify(string? key, string publicKeySpki)
    {
        var text = Normalize(key);
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var bytes = FromBase64Url(text[Prefix.Length..]);
        if (bytes is not { Length: PayloadBytes + SignatureBytes } || bytes[0] != 1) return null;
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
        var payload = bytes.AsSpan(0, PayloadBytes);
        if (!ecdsa.VerifyData(payload, bytes.AsSpan(PayloadBytes), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return null;
        return new LicenceKeyInfo(Convert.ToHexStringLower(payload[2..14]), payload[1], Epoch.AddDays((payload[14] << 8) | payload[15]));
    }

    /// <summary>The key as typed or pasted, without spaces and line breaks.</summary>
    public static string Normalize(string? key) => new((key ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static byte[]? FromBase64Url(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var b64 = text.Replace('-', '+').Replace('_', '/');
        try
        {
            return Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// <see cref="ILicenceService"/> against the HDMI Captain licence service (services/licence: Stripe Checkout, keys,
/// activations). The machine is identified by a salted SHA-256 of /etc/machine-id, never the raw id.
/// </summary>
public sealed class StripeLicenceService : ILicenceService
{
    /// <summary>The deployed service (services/licence/README.md › Deploy). Placeholder until it is deployed.</summary>
    public const string DefaultApiBase = "https://hdmicaptain-licence.example.workers.dev";

    /// <summary>The service's public signing key (services/licence/tools/genkey.mjs, 2026-10-26).</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE+xp3x61u0ob/y1wqLea9BryCdhOK5mhbv77qtUrTYcv5TmX+lXWRjU/Lfm82MY3exULu5bQI7EDB8lVProOu6Q==";

    private readonly HttpClient _http;
    private readonly string _publicKey;
    private readonly string _machine;

    public StripeLicenceService(string apiBase = DefaultApiBase, string publicKey = PublicKey, HttpMessageHandler? handler = null, string? machineId = null)
    {
        ApiBase = apiBase.TrimEnd('/');
        _publicKey = publicKey;
        _http = new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = new Uri(ApiBase + "/"), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HDMICaptain-Linux/1.0");
        _machine = MachineHash(machineId ?? ReadMachineId());
    }

    public string ApiBase { get; }

    /// <summary>Where Buy Pro… goes: the service creates the Checkout Session and redirects to Stripe.</summary>
    public string BuyUrl => $"{ApiBase}/buy?platform=linux";

    /// <summary>The DEBUG override for local testing (services/licence/dev/server.mjs); release builds always use the defaults.</summary>
    public static StripeLicenceService FromEnvironment()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("HDMICAPTAIN_LICENCE_API") is { Length: > 0 } api)
            return new StripeLicenceService(api, Environment.GetEnvironmentVariable("HDMICAPTAIN_LICENCE_PUBLIC_KEY") is { Length: > 0 } key ? key : PublicKey);
#endif
        return new StripeLicenceService();
    }

    public async Task<ActivationResult> ActivateAsync(string key, string machineName, CancellationToken cancel = default)
    {
        key = LicenceKey.Normalize(key);
        if (LicenceKey.Verify(key, _publicKey) == null)
            return new ActivationResult(null, "That isn't an HDMI Captain licence key. Copy it again from your purchase page.");
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/activate",
                new ActivateRequest(key, _machine, machineName.Length > 64 ? machineName[..64] : machineName), LicenceApiJson.Default.ActivateRequest, cancel);
            var body = await ReadAsync(response, cancel);
            if (response.IsSuccessStatusCode && body?.InstanceId is { Length: > 0 } instance)
                return new ActivationResult(new LicenceRecord(key, instance, DateTimeOffset.UtcNow), null);
            return new ActivationResult(null, (response.StatusCode, body?.Error) switch
            {
                (HttpStatusCode.Conflict, _) => $"This key is already active on {body?.Max ?? 3} computers. Deactivate it on one of them first.",
                (HttpStatusCode.Forbidden, _) => "This key is no longer valid because its purchase was refunded or disputed.",
                (HttpStatusCode.NotFound, _) => "This key isn't known to the licence server. Contact support with your receipt.",
                _ => "The licence server couldn't check the key right now. Try again later.",
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            LinuxLog.Write($"licence: activate failed: {e.GetType().Name}: {e.Message}");
            return new ActivationResult(null, "Couldn't reach the licence server. Check your internet connection and try again.");
        }
    }

    public async Task<LicenseCheck> ValidateAsync(LicenceRecord record, CancellationToken cancel = default)
    {
        // A cached record whose key no longer verifies (edited file, another build's key) is not Pro.
        if (LicenceKey.Verify(record.Key, _publicKey) == null) return LicenseCheck.NotOwned;
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/validate", new ValidateRequest(record.Key, record.InstanceId), LicenceApiJson.Default.ValidateRequest, cancel);
            var body = await ReadAsync(response, cancel);
            return (response.IsSuccessStatusCode, body?.Status) switch
            {
                (true, "active") => LicenseCheck.Owned,
                (true, "revoked" or "inactive") => LicenseCheck.NotOwned,
                // Unknown to the server (its data lost?) or a server error: keep the cached licence within the grace.
                _ => LicenseCheck.Unavailable,
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return LicenseCheck.Unavailable;
        }
    }

    public async Task DeactivateAsync(LicenceRecord record, CancellationToken cancel = default)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/deactivate", new ValidateRequest(record.Key, record.InstanceId), LicenceApiJson.Default.ValidateRequest, cancel);
            if (!response.IsSuccessStatusCode) LinuxLog.Write($"licence: deactivate answered {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The seat stays taken on the server; the key still works here only until the user activates again.
            LinuxLog.Write($"licence: deactivate failed offline: {e.Message}");
        }
    }

    private static async Task<ApiResponse?> ReadAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json") return null;
        return await response.Content.ReadFromJsonAsync(LicenceApiJson.Default.ApiResponse, cancel);
    }

    private static string ReadMachineId()
    {
        foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id) return id;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        // No machine id (unusual containers): a random one kept with the licence.
        var file = Path.Combine(LicenceStore.DataHome(), "hdmicaptain", "machine-id");
        try
        {
            if (File.Exists(file)) return File.ReadAllText(file).Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            File.WriteAllText(file, id);
            return id;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Environment.MachineName;
        }
    }

    /// <summary>What the server sees of this computer: stable, but not the machine id itself (salted, one-way).</summary>
    public static string MachineHash(string machineId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"hdmicaptain-licence-machine:{machineId}")));
}

internal sealed record ActivateRequest(string Key, string Machine, string Name);
internal sealed record ValidateRequest(string Key, string InstanceId);
internal sealed record ApiResponse(string? InstanceId, string? Status, string? Error, int? Max);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ActivateRequest))]
[JsonSerializable(typeof(ValidateRequest))]
[JsonSerializable(typeof(ApiResponse))]
internal sealed partial class LicenceApiJson : JsonSerializerContext;
