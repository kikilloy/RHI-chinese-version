using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// Digest lookups must degrade quietly: no answer from GitHub means "unverified", never
/// "installation blocked". The 24 h cache is what keeps the unauthenticated API budget intact.
/// </summary>
public class DownloadIntegrityGuardTests : IDisposable
{
    private static readonly Uri AssetUri =
        new("https://github.com/o/r/releases/download/v1/a.dll");

    private const string ReleaseJson = """
    { "assets": [ { "name": "a.dll", "size": 42, "digest": "sha256:788186855A4B733D32077EB5CC7248A04ECC5B0BFC05B87FAD53889EB2E37BBF" } ] }
    """;

    public void Dispose() => MirrorRuntime.Reset();

    private static GitHubDigestCache MemoryCache() =>
        new(() => DateTimeOffset.UtcNow, loadFromDisk: false, persist: false);

    [Fact]
    public async Task ReleaseAsset_ReturnsSha256WithSize()
    {
        var guard = new DownloadIntegrityGuard(MemoryCache());
        var calls = 0;

        var expectation = await guard.GetExpectationAsync(AssetUri, 42, (_, _) =>
        {
            calls++;
            return Task.FromResult<string?>(ReleaseJson);
        }, CancellationToken.None);

        Assert.NotNull(expectation);
        Assert.Equal(IntegrityAlgorithm.Sha256, expectation!.Algorithm);
        Assert.Equal("788186855a4b733d32077eb5cc7248a04ecc5b0bfc05b87fad53889eb2e37bbf", expectation.ExpectedHex);
        Assert.Equal(42, expectation.ExpectedLength);

        // Second lookup is served from cache — the API budget is precious.
        await guard.GetExpectationAsync(AssetUri, 42, (_, _) => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NoDigestInResponse_DegradesToNull()
    {
        var guard = new DownloadIntegrityGuard(MemoryCache());

        var expectation = await guard.GetExpectationAsync(AssetUri, 1, (_, _) =>
            Task.FromResult<string?>("{ assets-missing-digest }"),
            CancellationToken.None);

        Assert.Null(expectation);
    }

    [Fact]
    public async Task ApiFailure_DegradesToNull()
    {
        var guard = new DownloadIntegrityGuard(MemoryCache());

        var expectation = await guard.GetExpectationAsync(AssetUri, 1,
            (_, _) => Task.FromResult<string?>(null), CancellationToken.None);

        Assert.Null(expectation);
    }

    [Fact]
    public async Task VerificationDisabled_ReturnsNullWithoutCallingApi()
    {
        MirrorRuntime.Apply(true, null, false, verifyDownloads: false);
        var guard = new DownloadIntegrityGuard(MemoryCache());
        var called = false;

        var expectation = await guard.GetExpectationAsync(AssetUri, 1, (_, _) =>
        {
            called = true;
            return Task.FromResult<string?>(ReleaseJson);
        }, CancellationToken.None);

        Assert.Null(expectation);
        Assert.False(called);
    }

    [Fact]
    public async Task RawFile_UsesBlobSha_WhenLengthKnown()
    {
        var guard = new DownloadIntegrityGuard(MemoryCache());
        var raw = new Uri("https://raw.githubusercontent.com/o/r/main/manifest.json");

        var expectation = await guard.GetExpectationAsync(raw, 186, (_, _) =>
            Task.FromResult<string?>("{ \"sha\": \"08EE5BBAFCFBA6AA5059A8CC9464B5ECF50BDB84\", \"size\": 186 }"),
            CancellationToken.None);

        Assert.NotNull(expectation);
        Assert.Equal(IntegrityAlgorithm.GitBlobSha1, expectation!.Algorithm);
        Assert.Equal(186, expectation.BlobLength);
    }

    [Fact]
    public async Task RawFile_WithoutLength_IsSkipped()
    {
        var guard = new DownloadIntegrityGuard(MemoryCache());
        var raw = new Uri("https://raw.githubusercontent.com/o/r/main/manifest.json");
        var called = false;

        var expectation = await guard.GetExpectationAsync(raw, null, (_, _) =>
        {
            called = true;
            return Task.FromResult<string?>(null);
        }, CancellationToken.None);

        Assert.Null(expectation);
        Assert.False(called);
    }

    [Fact]
    public async Task UnrelatedUrl_ReturnsNull()
    {
        var guard = new DownloadIntegrityGuard(MemoryCache());

        var expectation = await guard.GetExpectationAsync(
            new Uri("https://www.nexusmods.com/site/mods/1"), 10,
            (_, _) => Task.FromResult<string?>(ReleaseJson), CancellationToken.None);

        Assert.Null(expectation);
    }
}
