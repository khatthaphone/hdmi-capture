using HDMICaptain.Core;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Services.Store;

namespace HDMICaptain.App.Store;

/// <summary>Where the Pro add-on is bought and its licence checked (spec §13.8, task 099).</summary>
public interface IProStore
{
    /// <summary>Localized price ("$9.99"), or null until the store answers.</summary>
    string? Price { get; }

    Task<LicenseCheck> CheckAsync();

    /// <summary>Shows the store's purchase UI. Owned when it went through (or was already owned).</summary>
    Task<LicenseCheck> PurchaseAsync();
}

/// <summary>
/// Microsoft Store: the durable add-on whose in-app offer token is <see cref="OfferToken"/>. Needs package identity,
/// so an unpackaged Debug run reports <see cref="LicenseCheck.Unavailable"/> and free features carry on.
/// </summary>
public sealed class MicrosoftStoreProStore(nint hwnd) : IProStore
{
    public const string OfferToken = "pro";

    private StoreContext? _context;
    private StoreProduct? _product;

    public string? Price => _product?.Price.FormattedPrice;

    private StoreContext Context()
    {
        if (_context != null) return _context;
        _context = StoreContext.GetDefault();
        WinRT.Interop.InitializeWithWindow.Initialize(_context, hwnd); // desktop apps: purchase UI needs an owner window
        return _context;
    }

    public async Task<LicenseCheck> CheckAsync()
    {
        try
        {
            var license = await Context().GetAppLicenseAsync();
            var products = await Context().GetAssociatedStoreProductsAsync(["Durable"]);
            _product = products.Products.Values.FirstOrDefault(p => p.InAppOfferToken == OfferToken);
            var owned = license.AddOnLicenses.Values.Any(l => l.InAppOfferToken == OfferToken && l.IsActive);
            return owned ? LicenseCheck.Owned : products.ExtendedError is null ? LicenseCheck.NotOwned : LicenseCheck.Unavailable;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Write($"[store] check failed: {e.Message}");
            return LicenseCheck.Unavailable;
        }
    }

    public async Task<LicenseCheck> PurchaseAsync()
    {
        if (_product is null && await CheckAsync() == LicenseCheck.Owned) return LicenseCheck.Owned;
        if (_product is null) return LicenseCheck.Unavailable;
        var result = await Context().RequestPurchaseAsync(_product.StoreId);
        AppLog.Write($"[store] purchase: {result.Status}");
        return result.Status switch
        {
            StorePurchaseStatus.Succeeded or StorePurchaseStatus.AlreadyPurchased => LicenseCheck.Owned,
            StorePurchaseStatus.NotPurchased => LicenseCheck.NotOwned,
            _ => LicenseCheck.Unavailable,
        };
    }
}

#if DEBUG
/// <summary>DEBUG stand-in (unpackaged runs, -debugUnlockPro YES): purchases succeed at once.</summary>
public sealed class FakeProStore(bool owned) : IProStore
{
    private bool _owned = owned;

    public string? Price => "$9.99";

    public Task<LicenseCheck> CheckAsync() => Task.FromResult(_owned ? LicenseCheck.Owned : LicenseCheck.NotOwned);

    public Task<LicenseCheck> PurchaseAsync()
    {
        _owned = true;
        return Task.FromResult(LicenseCheck.Owned);
    }
}
#endif

/// <summary>
/// The one Pro gate: Record and Save Recent ask it; free features never do. Keeps the entitlement cached with
/// DPAPI (current user) in %LOCALAPPDATA%\HDMICaptain\entitlement.bin so an owner is never locked out offline.
/// </summary>
public sealed class ProGate
{
    private readonly IProStore _store;
    private readonly string _cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDMICaptain", "entitlement.bin");
    private ProEntitlement _entitlement = new(null);

    public ProGate(IProStore store) => _store = store;

    public bool IsPro => _entitlement.IsPro;

    public string? Price => _store.Price;

    /// <summary>Pro turned on or off (UI thread).</summary>
    public event Action? Changed;

    public bool IsUnlocked(ProFeature feature) => _entitlement.IsUnlocked(feature);

    /// <summary>Loads the cache, then re-verifies with the store (launch, and when the app comes back online).</summary>
    public async Task RefreshAsync()
    {
        _entitlement = new ProEntitlement(await ReadCacheAsync());
        var before = IsPro;
        await ApplyAsync(await _store.CheckAsync());
        if (IsPro != before) Changed?.Invoke();
    }

    public async Task<bool> PurchaseAsync()
    {
        var before = IsPro;
        await ApplyAsync(await _store.PurchaseAsync());
        if (IsPro != before) Changed?.Invoke();
        return IsPro;
    }

    /// <summary>Restore = ask the store again.</summary>
    public Task RestoreAsync() => RefreshAsync();

    private async Task ApplyAsync(LicenseCheck check)
    {
        if (_entitlement.Apply(check) is not { } plain) return;
        try
        {
            var provider = new DataProtectionProvider("LOCAL=user");
            var buffer = await provider.ProtectAsync(CryptographicBuffer.CreateFromByteArray(plain));
            CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await File.WriteAllBytesAsync(_cachePath, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write($"[store] couldn't cache the entitlement: {e.Message}");
        }
    }

    private async Task<byte[]?> ReadCacheAsync()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var provider = new DataProtectionProvider();
            var plain = await provider.UnprotectAsync(CryptographicBuffer.CreateFromByteArray(await File.ReadAllBytesAsync(_cachePath)));
            CryptographicBuffer.CopyToByteArray(plain, out var bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null; // damaged or from another user: treat as no cache
        }
    }
}
