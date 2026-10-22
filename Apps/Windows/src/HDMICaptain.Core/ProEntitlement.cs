namespace HDMICaptain.Core;

/// <summary>What the store said when asked, if it could be asked at all.</summary>
public enum LicenseCheck
{
    /// <summary>The store answered: the Pro add-on is owned.</summary>
    Owned,
    /// <summary>The store answered: not owned.</summary>
    NotOwned,
    /// <summary>The store couldn't be reached (offline, no package identity, service error).</summary>
    Unavailable,
}

/// <summary>
/// The Pro entitlement and its local cache (task 099). Pure: the caller supplies the store answer and the
/// protected cache bytes (DPAPI in the app). Rules: a definite store answer wins; when the store is unreachable,
/// the cached answer stands, so an owner is never locked out offline.
/// </summary>
public sealed class ProEntitlement
{
    private const string OwnedMarker = "hdmicaptain.pro.v1:owned";

    public ProEntitlement(byte[]? cachedPlain) => IsPro = cachedPlain is { } bytes && System.Text.Encoding.UTF8.GetString(bytes) == OwnedMarker;

    public bool IsPro { get; private set; }

    public bool IsUnlocked(ProFeature feature) => IsPro;

    /// <summary>Applies a store answer; returns the bytes to cache (protect them before writing), or null to keep the cache.</summary>
    public byte[]? Apply(LicenseCheck check)
    {
        switch (check)
        {
            case LicenseCheck.Owned:
                IsPro = true;
                return System.Text.Encoding.UTF8.GetBytes(OwnedMarker);
            case LicenseCheck.NotOwned:
                IsPro = false;
                return [];
            default:
                return null; // unreachable store: keep whatever we knew
        }
    }
}
