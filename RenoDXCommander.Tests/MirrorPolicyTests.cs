using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// MirrorPolicy decides which GitHub URLs go through a mirror prefix. Getting this wrong is
/// expensive in both directions: proxying api.github.com breaks ETag/304 + rate limits + the
/// digest used for integrity checks, while failing to proxy a release asset leaves Chinese
/// users on a 3-minute-and-not-finished download. Everything here must be decidable offline.
/// </summary>
public class MirrorPolicyTests
{
    private static readonly string[] Prefixes =
    {
        "https://gh.nxnow.top/",
        "https://ghfile.geekertao.top/",
        "https://gh-proxy.com/",
    };

    private static MirrorOptions Enabled(params string[] prefixes) =>
        new(true, prefixes.Length == 0 ? Prefixes : prefixes);

    private static Uri U(string url) => new(url);

    // ── which URLs are proxyable ─────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-devkit.addon64")]
    [InlineData("https://github.com/RankFTW/RHI/archive/refs/heads/main.tar.gz")]
    [InlineData("https://codeload.github.com/RankFTW/RHI/tar.gz/refs/heads/main")]
    [InlineData("https://raw.githubusercontent.com/RankFTW/RHI/main/manifest.json")]
    public void GitHubDownloads_AreProxyable(string url) =>
        Assert.True(MirrorPolicy.ShouldProxy(U(url)), url + " should be proxyable");

    [Theory]
    [InlineData("https://api.github.com/repos/clshortfuse/renodx/releases/tags/snapshot")]
    [InlineData("https://api.github.com/repos/RankFTW/RHI/contents/manifest.json")]
    [InlineData("https://github.com/RankFTW/RHI")]                              // repo home, not a download
    [InlineData("https://nightly.link/RankFTW/RHI/workflows/build/main/RHI.zip")]
    [InlineData("https://www.nexusmods.com/site/mods/1")]
    [InlineData("https://reshade.me/downloads/ReShade_Setup_6.6.2.exe")]
    [InlineData("https://www.pcgamingwiki.com/wiki/Game")]
    [InlineData("http://github.com/o/r/releases/download/v1/a.dll")]            // https only
    public void ApiAndNonGitHubHosts_AreNotProxyable(string url) =>
        Assert.False(MirrorPolicy.ShouldProxy(U(url)), url + " must never be proxied");

    [Fact]
    public void PresignedReleaseAssetUrls_AreNotProxyable()
    {
        // The 302 target of a release download. The prefix mirror already fetches these
        // server-side, and the signature expires (se=…) — rewriting them just fails.
        var uri = U("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r&sig=abc&se=2026-11-01T07%3A19%3A59Z");
        Assert.False(MirrorPolicy.ShouldProxy(uri));
    }

    // ── attempt ordering ─────────────────────────────────────────────────────

    [Fact]
    public void Disabled_OnlyDirectAttempt()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://github.com/o/r/releases/download/v1/a.dll"),
            MirrorOptions.Direct);

        var only = Assert.Single(attempts);
        Assert.Equal(MirrorKind.Direct, only.Kind);
        Assert.Equal("https://github.com/o/r/releases/download/v1/a.dll", only.Uri.AbsoluteUri);
    }

    [Fact]
    public void Enabled_MirrorsInConfiguredOrder_DirectLast()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://github.com/o/r/releases/download/v1/a.dll"),
            Enabled());

        Assert.Equal(4, attempts.Count);
        Assert.Equal("https://gh.nxnow.top/https://github.com/o/r/releases/download/v1/a.dll", attempts[0].Uri.AbsoluteUri);
        Assert.Equal("https://ghfile.geekertao.top/https://github.com/o/r/releases/download/v1/a.dll", attempts[1].Uri.AbsoluteUri);
        Assert.Equal("https://gh-proxy.com/https://github.com/o/r/releases/download/v1/a.dll", attempts[2].Uri.AbsoluteUri);
        Assert.Equal(MirrorKind.Direct, attempts[3].Kind);
        Assert.All(attempts.Take(3), a => Assert.Equal(MirrorKind.Prefix, a.Kind));
    }

    [Fact]
    public void CustomPrefix_WinsOverDefaults()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://raw.githubusercontent.com/o/r/main/motd.md"),
            Enabled("https://my.mirror.example/"));

        Assert.Equal("https://my.mirror.example/https://raw.githubusercontent.com/o/r/main/motd.md",
            attempts[0].Uri.AbsoluteUri);
        Assert.DoesNotContain(attempts, a => a.Uri.Host == "gh.nxnow.top");
    }

    [Fact]
    public void EmptyPrefixList_StillFallsBackToDirect()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://raw.githubusercontent.com/o/r/main/motd.md"),
            new MirrorOptions(true, Array.Empty<string>()));

        var only = Assert.Single(attempts);
        Assert.Equal(MirrorKind.Direct, only.Kind);
    }

    [Fact]
    public void NonProxyableUrl_IsDirectEvenWhenEnabled()
    {
        var uri = U("https://api.github.com/repos/o/r/releases/tags/v1");
        var attempts = MirrorPolicy.BuildAttempts(uri, Enabled());

        var only = Assert.Single(attempts);
        Assert.Equal(MirrorKind.Direct, only.Kind);
        Assert.Equal(uri, only.Uri);
    }

    [Fact]
    public void QueryString_IsPreserved()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://raw.githubusercontent.com/o/r/main/doc.md?token=abc&x=1"),
            Enabled("https://gh.nxnow.top/"));

        Assert.Contains("?token=abc&x=1", attempts[0].Uri.AbsoluteUri);
    }

    // ── idempotence / loop guards ────────────────────────────────────────────

    [Fact]
    public void AlreadyMirrored_IsNotDoubleWrapped()
    {
        var once = MirrorPolicy.BuildAttempts(
            U("https://github.com/o/r/releases/download/v1/a.dll"), Enabled());

        var twice = MirrorPolicy.BuildAttempts(once[0].Uri, Enabled());

        var only = Assert.Single(twice);
        Assert.Equal(MirrorKind.Direct, only.Kind);
        Assert.Equal(once[0].Uri, only.Uri);
    }

    [Fact]
    public void MirrorHostWithoutPrefix_IsNotDoubleWrapped()
    {
        // e.g. a mirror's own API/health endpoint — same host, different shape.
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://gh.nxnow.top/health"), Enabled());
        Assert.Single(attempts);
    }

    [Theory]
    [InlineData("http://gh.nxnow.top/")]              // https only
    [InlineData("gh.nxnow.top/")]                      // not absolute
    [InlineData("")]                                   // empty
    [InlineData(null)]                                 // null
    [InlineData("https://github.com/")]                // github host = loop
    [InlineData("https://raw.githubusercontent.com/")] // github host = loop
    public void UnusablePrefix_IsRejected(string? prefix)
    {
        Assert.False(MirrorPolicy.TryNormalizePrefix(prefix, out _));
        Assert.False(MirrorPolicy.TryBuildPrefixed(
            U("https://github.com/o/r/releases/download/v1/a.dll"), prefix, out _));
    }

    [Fact]
    public void PrefixWithoutTrailingSlash_IsNormalised()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://github.com/o/r/releases/download/v1/a.dll"),
            Enabled("https://gh.nxnow.top"));

        Assert.Equal("https://gh.nxnow.top/https://github.com/o/r/releases/download/v1/a.dll",
            attempts[0].Uri.AbsoluteUri);
    }

    // ── jsDelivr fallback (off by default) ───────────────────────────────────

    [Fact]
    public void JsDelivr_IsOffByDefault()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://raw.githubusercontent.com/o/r/main/manifest.json"),
            Enabled());

        Assert.DoesNotContain(attempts, a => a.Kind == MirrorKind.JsDelivr);
    }

    [Fact]
    public void JsDelivr_WhenEnabled_IsTriedAfterPrefixes_BeforeDirect()
    {
        var attempts = MirrorPolicy.BuildAttempts(
            U("https://raw.githubusercontent.com/RankFTW/RHI/main/manifest.json"),
            new MirrorOptions(true, Prefixes, UseJsDelivrFallback: true));

        Assert.Equal(5, attempts.Count);
        Assert.All(attempts.Take(3), a => Assert.Equal(MirrorKind.Prefix, a.Kind));
        Assert.Equal(MirrorKind.JsDelivr, attempts[3].Kind);
        Assert.Equal("https://cdn.jsdelivr.net/gh/RankFTW/RHI@main/manifest.json", attempts[3].Uri.AbsoluteUri);
        Assert.Equal(MirrorKind.Direct, attempts[4].Kind);
    }

    [Fact]
    public void JsDelivr_KeepsNestedPath()
    {
        Assert.True(MirrorPolicy.TryBuildJsDelivr(
            U("https://raw.githubusercontent.com/RankFTW/RHI/main/engine-files/foo/bar.ini"),
            out var jsdelivr));
        Assert.Equal("https://cdn.jsdelivr.net/gh/RankFTW/RHI@main/engine-files/foo/bar.ini", jsdelivr.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://github.com/o/r/releases/download/v1/a.dll")]  // not raw
    [InlineData("https://raw.githubusercontent.com/o/r")]             // too few segments
    [InlineData("https://raw.githubusercontent.com/o/r/main")]        // no file path
    public void JsDelivr_OnlyForRawFileUrls(string url) =>
        Assert.False(MirrorPolicy.TryBuildJsDelivr(U(url), out _));

    // ── helper surface ───────────────────────────────────────────────────────

    [Fact]
    public void TryRewrite_ReportsKind()
    {
        Assert.True(MirrorPolicy.TryRewrite(
            U("https://github.com/o/r/releases/download/v1/a.dll"), Enabled(),
            out var rewritten, out var kind));
        Assert.Equal(MirrorKind.Prefix, kind);
        Assert.StartsWith("https://gh.nxnow.top/", rewritten!.AbsoluteUri);

        Assert.False(MirrorPolicy.TryRewrite(
            U("https://api.github.com/repos/o/r"), Enabled(), out _, out var kind2));
        Assert.Equal(MirrorKind.Direct, kind2);
    }

    [Fact]
    public void PrefixHost_ForLogging()
    {
        Assert.Equal("gh.nxnow.top", MirrorPolicy.PrefixHost("https://gh.nxnow.top/"));
        Assert.Equal(string.Empty, MirrorPolicy.PrefixHost("not a url"));
    }

    [Fact]
    public void DefaultPrefixes_AreAllUsableHttpsMirrors()
    {
        Assert.NotEmpty(MirrorPolicy.DefaultPrefixes);
        Assert.All(MirrorPolicy.DefaultPrefixes, p =>
        {
            Assert.True(MirrorPolicy.TryNormalizePrefix(p, out var normalized), p);
            Assert.StartsWith("https://", normalized, StringComparison.Ordinal);
            Assert.False(MirrorPolicy.IsGitHubHost(MirrorPolicy.PrefixHost(p)), p);
        });
    }

    [Fact]
    public void NullInputs_AreSafe()
    {
        Assert.Empty(MirrorPolicy.BuildAttempts(null, Enabled()));
        Assert.False(MirrorPolicy.ShouldProxy(null));
        Assert.False(MirrorPolicy.TryBuildPrefixed(null, "https://gh.nxnow.top/", out _));
        Assert.False(MirrorPolicy.TryBuildJsDelivr(null, out _));
        Assert.False(MirrorPolicy.IsAlreadyProxied(null, Prefixes));
        // Null options = mirroring off, not "no attempts": direct must always remain.
        var attempts = MirrorPolicy.BuildAttempts(U("https://github.com/o/r/releases/download/v1/a.dll"), null);
        var only = Assert.Single(attempts);
        Assert.Equal(MirrorKind.Direct, only.Kind);
    }
}
