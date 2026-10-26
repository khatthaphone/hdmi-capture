using HDMICaptain.Core;

namespace HDMICaptain.Linux.App.Tests;

public sealed class LicenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Directory.CreateTempSubdirectory("hc-licence").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void OfflineOwnersKeepProForTheGracePeriod()
    {
        var record = new LicenceRecord("K", "i", Now);
        Assert.True(LicencePolicy.IsPro(record, Now + TimeSpan.FromDays(29)));
        Assert.False(LicencePolicy.IsPro(record, Now + TimeSpan.FromDays(31)));
        Assert.Same(record, LicencePolicy.Apply(record, LicenseCheck.Unavailable, Now + TimeSpan.FromDays(5)));
    }

    [Fact]
    public void ADefiniteAnswerWins()
    {
        var record = new LicenceRecord("K", "i", Now - TimeSpan.FromDays(10));
        Assert.Equal(Now, LicencePolicy.Apply(record, LicenseCheck.Owned, Now)!.ValidatedAt);
        Assert.Null(LicencePolicy.Apply(record, LicenseCheck.NotOwned, Now)); // revoked or refunded
    }

    [Fact]
    public async Task ActivationIsCachedAndDeactivationForgetsIt()
    {
        var store = new LicenceStore(new FakeLicenceService(), folder: _folder);
        Assert.False(store.IsUnlocked(ProFeature.Recording));
        Assert.NotNull(await store.ActivateAsync("not-a-key"));
        Assert.Null(await store.ActivateAsync("HC-TEST-0001"));
        Assert.True(new LicenceStore(new FakeLicenceService(), folder: _folder).IsUnlocked(ProFeature.ReplayBuffer));
        var mode = File.GetUnixFileMode(Path.Combine(_folder, "licence.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        await store.DeactivateAsync();
        Assert.False(new LicenceStore(new FakeLicenceService(), folder: _folder).IsPro);
    }
}
