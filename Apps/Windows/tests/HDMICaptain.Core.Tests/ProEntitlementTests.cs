using HDMICaptain.Core;

namespace HDMICaptain.Core.Tests;

public class ProEntitlementTests
{
    [Fact]
    public void FreshInstallIsFree()
    {
        var pro = new ProEntitlement(null);

        Assert.False(pro.IsPro);
        Assert.False(pro.IsUnlocked(ProFeature.Recording));
    }

    [Fact]
    public void PurchaseUnlocksAndCachesAcrossLaunches()
    {
        var pro = new ProEntitlement(null);

        var cache = pro.Apply(LicenseCheck.Owned);

        Assert.True(pro.IsUnlocked(ProFeature.ReplayBuffer));
        Assert.True(new ProEntitlement(cache).IsPro);
    }

    [Fact]
    public void OwnerStaysUnlockedWhileTheStoreIsUnreachable()
    {
        var cache = new ProEntitlement(null).Apply(LicenseCheck.Owned);
        var nextLaunch = new ProEntitlement(cache);

        var write = nextLaunch.Apply(LicenseCheck.Unavailable);

        Assert.True(nextLaunch.IsPro);
        Assert.Null(write);
    }

    [Fact]
    public void RefundOrRevocationLocksAgain()
    {
        var pro = new ProEntitlement(new ProEntitlement(null).Apply(LicenseCheck.Owned));

        var cache = pro.Apply(LicenseCheck.NotOwned);

        Assert.False(pro.IsPro);
        Assert.False(new ProEntitlement(cache).IsPro);
    }

    [Fact]
    public void DamagedCacheIsFree()
    {
        Assert.False(new ProEntitlement([1, 2, 3]).IsPro);
    }
}
