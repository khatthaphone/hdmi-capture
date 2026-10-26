using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HDMICaptain.Core;

namespace HDMICaptain.Linux.App.Tests;

/// <summary>Licence keys and the client for the Stripe licence service (task 119; the service is services/licence).</summary>
public sealed partial class StripeLicenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepoRoot([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));

    private static (string PublicKey, string Key, string Id) Fixture()
    {
        // Written by services/licence/tools/fixture.mjs: a key signed in JavaScript by a throwaway key.
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "Apps/Linux/tests/HDMICaptain.Linux.App.Tests/Fixtures/licence-key.json")));
        var r = doc.RootElement;
        return (r.GetProperty("publicKey").GetString()!, r.GetProperty("key").GetString()!, r.GetProperty("id").GetString()!);
    }

    [Fact]
    public void KeysSignedByTheServiceVerifyHere()
    {
        var (publicKey, key, id) = Fixture();
        var info = LicenceKey.Verify(key, publicKey);
        Assert.NotNull(info);
        Assert.Equal(id, info.Id);
        Assert.Equal(new DateOnly(2026, 10, 26), info.Issued);
        // Pasted with line breaks and spaces.
        Assert.NotNull(LicenceKey.Verify($"  {key[..30]}\n{key[30..]} ", publicKey));
    }

    [Fact]
    public void TyposForgeriesAndOtherKeysFailOffline()
    {
        var (publicKey, key, _) = Fixture();
        var typo = key[..20] + (key[20] == 'A' ? 'B' : 'A') + key[21..];
        Assert.Null(LicenceKey.Verify(typo, publicKey));
        Assert.Null(LicenceKey.Verify(key, StripeLicenceService.PublicKey)); // signed by another service key
        Assert.Null(LicenceKey.Verify("HC-TEST-0001", publicKey));
        Assert.Null(LicenceKey.Verify("HC1-", publicKey));
        Assert.Null(LicenceKey.Verify("HC1-!!!!", publicKey));
        Assert.Null(LicenceKey.Verify(null, publicKey));
    }

    [Fact]
    public void TheMachineIdNeverLeavesAsIs()
    {
        var hash = StripeLicenceService.MachineHash("0123456789abcdef0123456789abcdef");
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.DoesNotContain("0123456789abcdef", hash);
        Assert.Equal(hash, StripeLicenceService.MachineHash("0123456789abcdef0123456789abcdef"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"instanceId":"abc"}""", null)]
    [InlineData(HttpStatusCode.Conflict, """{"error":"limit","max":3}""", "already active on 3 computers")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"revoked"}""", "refunded or disputed")]
    [InlineData(HttpStatusCode.NotFound, """{"error":"unknown_key"}""", "isn't known")]
    [InlineData(HttpStatusCode.BadGateway, "<html>", "couldn't check")]
    public async Task ActivationAnswersBecomeMessages(HttpStatusCode status, string body, string? message)
    {
        var (publicKey, key, _) = Fixture();
        var service = new StripeLicenceService("https://licence.test", publicKey, new Answer(status, body), "machine");
        var result = await service.ActivateAsync(key, "desk", Ct);
        if (message == null)
        {
            Assert.Equal("abc", result.Record?.InstanceId);
        }
        else
        {
            Assert.Null(result.Record);
            Assert.Contains(message, result.Error, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ABadKeyNeverReachesTheServer()
    {
        var (publicKey, _, _) = Fixture();
        var answer = new Answer(HttpStatusCode.OK, """{"instanceId":"abc"}""");
        var result = await new StripeLicenceService("https://licence.test", publicKey, answer, "machine").ActivateAsync("HC1-AAAA", "desk", Ct);
        Assert.Contains("isn't an HDMI Captain licence key", result.Error);
        Assert.Equal(0, answer.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"active"}""", LicenseCheck.Owned)]
    [InlineData(HttpStatusCode.OK, """{"status":"revoked"}""", LicenseCheck.NotOwned)]
    [InlineData(HttpStatusCode.OK, """{"status":"inactive"}""", LicenseCheck.NotOwned)]
    [InlineData(HttpStatusCode.NotFound, """{"status":"unknown"}""", LicenseCheck.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"server"}""", LicenseCheck.Unavailable)]
    public async Task ValidationAnswersMapToChecks(HttpStatusCode status, string body, LicenseCheck expected)
    {
        var (publicKey, key, _) = Fixture();
        var service = new StripeLicenceService("https://licence.test", publicKey, new Answer(status, body), "machine");
        Assert.Equal(expected, await service.ValidateAsync(new LicenceRecord(key, "abc", DateTimeOffset.UtcNow), Ct));
    }

    [Fact]
    public async Task OfflineValidationKeepsTheCachedLicence()
    {
        var (publicKey, key, _) = Fixture();
        var service = new StripeLicenceService("https://licence.test", publicKey, new Offline(), "machine");
        Assert.Equal(LicenseCheck.Unavailable, await service.ValidateAsync(new LicenceRecord(key, "abc", DateTimeOffset.UtcNow), Ct));
        Assert.Contains("Couldn't reach", (await service.ActivateAsync(key, "desk", Ct)).Error);
    }

    /// <summary>
    /// The whole purchase against services/licence/dev/server.mjs (the real Worker code, fake Stripe): buy → success page
    /// → activate → validate → seat limit → deactivate → refund revokes. Needs Node; skipped without it.
    /// </summary>
    [Fact]
    public async Task BuyActivateRefundEndToEnd()
    {
        var node = FindOnPath("node");
        if (node == null) Assert.Skip("node isn't installed");
        using var server = Process.Start(new ProcessStartInfo(node, ["dev/server.mjs", "--port", "0", "--max-activations", "2"])
        {
            WorkingDirectory = Path.Combine(RepoRoot(), "services", "licence"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        try
        {
            var ready = await server.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(20), Ct);
            var parts = ready!.Split(' ');
            Assert.Equal("listening", parts[0]);
            var (origin, publicKey) = (parts[1], parts[2]);

            var service = new StripeLicenceService(origin, publicKey, machineId: "e2e-machine-1");
            using var browser = new HttpClient();
            using var page = await browser.GetAsync(service.BuyUrl, Ct); // → fake checkout → paid → webhook → success page
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(Ct);
            var key = KeyPattern().Match(html).Value;
            Assert.NotNull(LicenceKey.Verify(key, publicKey));
            var session = page.RequestMessage!.RequestUri!.Query.Split("session_id=")[1];

            var activation = await service.ActivateAsync(key, "e2e desk", Ct);
            Assert.Null(activation.Error);
            var record = activation.Record!;
            Assert.Equal(LicenseCheck.Owned, await service.ValidateAsync(record, Ct));
            Assert.Equal(record.InstanceId, (await service.ActivateAsync(key, "again", Ct)).Record?.InstanceId);

            var second = new StripeLicenceService(origin, publicKey, machineId: "e2e-machine-2");
            Assert.Null((await second.ActivateAsync(key, "laptop", Ct)).Error);
            var third = new StripeLicenceService(origin, publicKey, machineId: "e2e-machine-3");
            Assert.Contains("already active on 2 computers", (await third.ActivateAsync(key, "spare", Ct)).Error);
            await service.DeactivateAsync(record, Ct);
            Assert.Equal(LicenseCheck.NotOwned, await service.ValidateAsync(record, Ct));
            Assert.Null((await third.ActivateAsync(key, "spare", Ct)).Error);

            using var refund = await browser.PostAsync($"{origin}/fake-stripe/refund/pi_{session["cs_test_".Length..]}", new StringContent(""), Ct);
            Assert.True(refund.IsSuccessStatusCode);
            var thirdRecord = (await third.ActivateAsync(key, "spare", Ct)).Record;
            Assert.Null(thirdRecord); // revoked: activation refused
            Assert.Equal(LicenseCheck.NotOwned, await second.ValidateAsync(new LicenceRecord(key, "any", DateTimeOffset.UtcNow), Ct));
        }
        finally
        {
            server.Kill();
        }
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);

    [GeneratedRegex("HC1-[A-Za-z0-9_-]{107}")]
    private static partial Regex KeyPattern();

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var json = body.StartsWith('{');
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, json ? "application/json" : "text/html"),
            });
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Network is unreachable");
    }
}
