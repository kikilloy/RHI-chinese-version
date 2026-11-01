using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// "Test mirrors" must never hang the settings page: unusable prefixes are reported immediately,
/// and an empty list does nothing at all.
/// </summary>
public class MirrorProbeTests
{
    [Fact]
    public async Task EmptyList_ReturnsNoResults()
    {
        Assert.Empty(await MirrorProbe.TestAsync(Array.Empty<string>()));
        Assert.Empty(await MirrorProbe.TestAsync(null!));
    }

    [Fact]
    public async Task UnusablePrefixes_AreReportedWithoutNetwork()
    {
        var results = await MirrorProbe.TestAsync(new[]
        {
            "not a url",
            "http://insecure.example/",
            "https://github.com/",     // would be a rewrite loop
        });

        Assert.Equal(3, results.Count);
        Assert.All(results, r =>
        {
            Assert.False(r.Ok);
            Assert.Contains("prefix", r.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task DuplicatePrefixes_AreProbedOnce()
    {
        var results = await MirrorProbe.TestAsync(new[]
        {
            "http://dupe.example/",
            "http://dupe.example/",
        });

        Assert.Single(results);
    }

    [Fact]
    public void HostIsExposedForDisplay()
    {
        Assert.Equal("gh.nxnow.top", new MirrorProbeResult("https://gh.nxnow.top/", true, 12, null).Host);
        Assert.Equal(string.Empty, new MirrorProbeResult("garbage", false, 0, "bad").Host);
    }
}
