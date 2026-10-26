using System.Text.Json;
using System.Text.Json.Serialization;
using HDMICaptain.Core;
using HDMICaptain.Linux.Media;

namespace HDMICaptain.Linux.App;

/// <summary>An activated licence as cached on disk: the key, the vendor's instance id and when it last validated.</summary>
public sealed record LicenceRecord(string Key, string InstanceId, DateTimeOffset ValidatedAt);

/// <summary>What activating a key gave: the record to cache, or why not.</summary>
public sealed record ActivationResult(LicenceRecord? Record, string? Error);

/// <summary>
/// The licence vendor (task 119: Stripe through our licence service, <see cref="StripeLicenceService"/>; ADR 0004).
/// Activate binds a key to this machine once (online); Validate re-checks it; Deactivate frees the seat.
/// </summary>
public interface ILicenceService
{
    /// <summary>Where Buy Pro… opens (null: buying isn't possible in this build).</summary>
    string? BuyUrl { get; }
    Task<ActivationResult> ActivateAsync(string key, string machineName, CancellationToken cancel = default);
    /// <summary>Owned, NotOwned (revoked/refunded), or Unavailable (offline, vendor down).</summary>
    Task<LicenseCheck> ValidateAsync(LicenceRecord record, CancellationToken cancel = default);
    /// <summary>Best effort: frees this computer's seat (offline, the seat stays taken).</summary>
    Task DeactivateAsync(LicenceRecord record, CancellationToken cancel = default);
}

/// <summary>
/// Pure rules (unit-tested): a definite answer wins; when the vendor can't be reached the cached licence stands for
/// <see cref="OfflineGrace"/> after its last successful validation, so an owner is never locked out by a flight.
/// </summary>
public static class LicencePolicy
{
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(30);
    public static readonly TimeSpan RevalidateAfter = TimeSpan.FromDays(3);

    public static bool IsPro(LicenceRecord? record, DateTimeOffset now) => record != null && now - record.ValidatedAt <= OfflineGrace;

    public static bool NeedsValidation(LicenceRecord record, DateTimeOffset now) => now - record.ValidatedAt >= RevalidateAfter;

    /// <summary>The record to keep after a validation answer (null = forget it).</summary>
    public static LicenceRecord? Apply(LicenceRecord record, LicenseCheck check, DateTimeOffset now) => check switch
    {
        LicenseCheck.Owned => record with { ValidatedAt = now },
        LicenseCheck.NotOwned => null,
        _ => record,
    };
}

/// <summary>
/// The Pro gate on Linux (the equivalent of EntitlementStore.gate): free features never ask it. The licence lives in
/// $XDG_DATA_HOME/hdmicaptain/licence.json (mode 0600). DEBUG builds unlock with -debugUnlockPro or the fake service.
/// </summary>
public sealed class LicenceStore
{
    private readonly string _path;
    private readonly ILicenceService _service;
    private readonly bool _debugUnlock;
    private LicenceRecord? _record;

    public LicenceStore(ILicenceService service, bool debugUnlock = false, string? folder = null)
    {
        _service = service;
        _debugUnlock = debugUnlock;
        _path = Path.Combine(folder ?? Path.Combine(DataHome(), "hdmicaptain"), "licence.json");
        _record = Read();
    }

    public event Action? Changed;

    public bool IsPro => _debugUnlock || LicencePolicy.IsPro(_record, DateTimeOffset.UtcNow);

    public bool IsUnlocked(ProFeature feature) => IsPro;

    public string? Key => _record?.Key;

    public string? BuyUrl => _service.BuyUrl;

    public async Task<string?> ActivateAsync(string key)
    {
        var result = await _service.ActivateAsync(key.Trim(), Environment.MachineName);
        if (result.Record == null) return result.Error ?? "The key wasn't accepted.";
        Save(result.Record);
        return null;
    }

    /// <summary>On launch and every few days: re-check with the vendor; offline keeps the cached licence.</summary>
    public async Task RevalidateAsync()
    {
        if (_record is not { } record || !LicencePolicy.NeedsValidation(record, DateTimeOffset.UtcNow)) return;
        LicenseCheck check;
        try
        {
            check = await _service.ValidateAsync(record);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            check = LicenseCheck.Unavailable;
        }
        Save(LicencePolicy.Apply(record, check, DateTimeOffset.UtcNow));
    }

    /// <summary>Forgets the licence here at once and frees the seat on the server when it can.</summary>
    public async Task DeactivateAsync()
    {
        var record = _record;
        Save(null);
        if (record != null) await _service.DeactivateAsync(record);
    }

    private void Save(LicenceRecord? record)
    {
        _record = record;
        try
        {
            if (record == null) File.Delete(_path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(record, LicenceJson.Default.LicenceRecord));
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LinuxLog.Write($"licence: couldn't save: {e.Message}");
        }
        Changed?.Invoke();
    }

    private LicenceRecord? Read()
    {
        try
        {
            return AtomicFile.ReadAllText(_path) is { } text ? JsonSerializer.Deserialize(text, LicenceJson.Default.LicenceRecord) : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    public static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
}

/// <summary>
/// DEBUG test keys (HC-TEST-…) without a server, in front of the real service (<paramref name="inner"/>) for every
/// other key. Release builds never accept test keys; with no inner service nothing else is accepted (tests).
/// </summary>
public sealed class FakeLicenceService(ILicenceService? inner = null) : ILicenceService
{
    private const string TestPrefix = "HC-TEST-";

    public string? BuyUrl => inner?.BuyUrl;

    public Task<ActivationResult> ActivateAsync(string key, string machineName, CancellationToken cancel = default)
    {
#if DEBUG
        if (key.StartsWith(TestPrefix, StringComparison.Ordinal))
            return Task.FromResult(new ActivationResult(new LicenceRecord(key, $"fake-{machineName}", DateTimeOffset.UtcNow), null));
#endif
        return inner?.ActivateAsync(key, machineName, cancel)
            ?? Task.FromResult(new ActivationResult(null, "Licence keys can't be checked in this build."));
    }

    public Task<LicenseCheck> ValidateAsync(LicenceRecord record, CancellationToken cancel = default)
    {
        if (record.Key.StartsWith(TestPrefix, StringComparison.Ordinal))
        {
#if DEBUG
            return Task.FromResult(LicenseCheck.Owned);
#else
            return Task.FromResult(LicenseCheck.NotOwned);
#endif
        }
        return inner?.ValidateAsync(record, cancel) ?? Task.FromResult(LicenseCheck.Unavailable);
    }

    public Task DeactivateAsync(LicenceRecord record, CancellationToken cancel = default) =>
        record.Key.StartsWith(TestPrefix, StringComparison.Ordinal) || inner == null ? Task.CompletedTask : inner.DeactivateAsync(record, cancel);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LicenceRecord))]
internal sealed partial class LicenceJson : JsonSerializerContext;
