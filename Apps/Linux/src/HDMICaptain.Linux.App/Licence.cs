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
/// The licence vendor (task 119; Lemon Squeezy License API or Paddle + Keygen — the user's decision, ADR 0004).
/// Activate binds a key to this machine once (online); Validate re-checks it.
/// </summary>
public interface ILicenceService
{
    Task<ActivationResult> ActivateAsync(string key, string machineName, CancellationToken cancel = default);
    /// <summary>Owned, NotOwned (revoked/refunded), or Unavailable (offline, vendor down).</summary>
    Task<LicenseCheck> ValidateAsync(LicenceRecord record, CancellationToken cancel = default);
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

    public void Deactivate() => Save(null);

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
/// Until the vendor is chosen: accepts DEBUG test keys (HC-TEST-…) and nothing else, so release builds stay free-only.
/// </summary>
public sealed class FakeLicenceService : ILicenceService
{
    public Task<ActivationResult> ActivateAsync(string key, string machineName, CancellationToken cancel = default) =>
        Task.FromResult(
#if DEBUG
            key.StartsWith("HC-TEST-", StringComparison.Ordinal)
                ? new ActivationResult(new LicenceRecord(key, $"fake-{machineName}", DateTimeOffset.UtcNow), null)
                :
#endif
                new ActivationResult(null, "Licence keys can't be checked yet in this build."));

    public Task<LicenseCheck> ValidateAsync(LicenceRecord record, CancellationToken cancel = default) =>
        Task.FromResult(record.Key.StartsWith("HC-TEST-", StringComparison.Ordinal) ? LicenseCheck.Owned : LicenseCheck.Unavailable);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LicenceRecord))]
internal sealed partial class LicenceJson : JsonSerializerContext;
