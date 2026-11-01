using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// MirrorRuntime is the single mutable source the HTTP pipeline reads. Priority is
/// custom prefix → manifest prefix list → built-in defaults, and it must never end up with an
/// unusable (http, relative, or GitHub) prefix.
/// </summary>
public class MirrorRuntimeTests : IDisposable
{
    public void Dispose() => MirrorRuntime.Reset();

    [Fact]
    public void Default_IsDirectAndVerifiesDownloads()
    {
        MirrorRuntime.Reset();
        Assert.False(MirrorRuntime.Options.Enabled);
        Assert.True(MirrorRuntime.VerifyDownloads);
    }

    [Fact]
    public void Enabled_WithoutCustomOrManifest_UsesBuiltInDefaults()
    {
        MirrorRuntime.Apply(true, customPrefix: null, useJsDelivrFallback: false, verifyDownloads: true);

        Assert.True(MirrorRuntime.Options.Enabled);
        Assert.Equal(MirrorPolicy.DefaultPrefixes, MirrorRuntime.Options.Prefixes);
    }

    [Fact]
    public void CustomPrefix_WinsOverManifest()
    {
        MirrorRuntime.SetManifestPrefixes(new[] { "https://from-manifest.example/" });
        MirrorRuntime.Apply(true, "https://custom.example/", false, true);

        var prefixes = MirrorRuntime.Options.Prefixes;
        Assert.Single(prefixes);
        Assert.Equal("https://custom.example/", prefixes[0]);
    }

    [Fact]
    public void ManifestPrefixes_WinOverBuiltInDefaults()
    {
        MirrorRuntime.SetManifestPrefixes(new[] { "https://from-manifest.example/", "https://second.example/" });
        MirrorRuntime.Apply(true, null, false, true);

        Assert.Equal(new[] { "https://from-manifest.example/", "https://second.example/" },
            MirrorRuntime.Options.Prefixes);
    }

    [Fact]
    public void UnusableCustomPrefix_FallsBackToManifest()
    {
        MirrorRuntime.SetManifestPrefixes(new[] { "https://from-manifest.example/" });
        MirrorRuntime.Apply(true, "http://insecure.example/", false, true);

        Assert.Equal(new[] { "https://from-manifest.example/" }, MirrorRuntime.Options.Prefixes);
    }

    [Fact]
    public void GitHubHostAsCustomPrefix_IsRejected()
    {
        MirrorRuntime.Apply(true, "https://github.com/", false, true);

        // Would be a rewrite loop — must never be honoured.
        Assert.Equal(MirrorPolicy.DefaultPrefixes, MirrorRuntime.Options.Prefixes);
    }

    [Fact]
    public void EmptyManifestList_KeepsExistingPrefixes()
    {
        MirrorRuntime.SetManifestPrefixes(new[] { "https://from-manifest.example/" });
        MirrorRuntime.SetManifestPrefixes(Array.Empty<string>());

        Assert.Equal(new[] { "https://from-manifest.example/" }, MirrorRuntime.EffectivePrefixes);
    }

    [Fact]
    public void InvalidManifestPrefixes_AreDropped()
    {
        MirrorRuntime.SetManifestPrefixes(new[] { "not a url", "http://insecure.example/", "https://good.example/" });

        Assert.Equal(new[] { "https://good.example/" }, MirrorRuntime.EffectivePrefixes);
    }

    [Fact]
    public void DuplicatePrefixes_AreCollapsed()
    {
        MirrorRuntime.SetManifestPrefixes(new[] { "https://a.example/", "https://a.example" });

        Assert.Single(MirrorRuntime.EffectivePrefixes);
    }

    [Fact]
    public void DisabledAfterEnabled_GoesBackToDirect()
    {
        MirrorRuntime.Apply(true, null, false, true);
        Assert.True(MirrorRuntime.Options.Enabled);

        MirrorRuntime.Apply(false, null, false, true);
        Assert.False(MirrorRuntime.Options.Enabled);
    }

    [Fact]
    public void State_RoundTripsForTheSettingsPage()
    {
        MirrorRuntime.Apply(true, "https://custom.example/", useJsDelivrFallback: true, verifyDownloads: false);

        var state = MirrorRuntime.State;
        Assert.True(state.Enabled);
        Assert.Equal("https://custom.example/", state.CustomPrefix);
        Assert.True(state.UseJsDelivrFallback);
        Assert.False(state.VerifyDownloads);
    }
}
