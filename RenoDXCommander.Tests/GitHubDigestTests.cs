using System.Security.Cryptography;
using System.Text;
using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// Integrity verification for mirrored downloads. The payloads below mirror real GitHub API
/// responses (checked against api.github.com while implementing), and the blob-sha test uses the
/// canonical git value for the string "test".
/// </summary>
public class GitHubDigestTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "snapshot",
      "assets": [
        { "name": "other.dll", "size": 10, "digest": "sha256:aaaa" },
        { "name": "renodx-devkit.addon64", "size": 5454336, "digest": "sha256:788186855A4B733D32077EB5CC7248A04ECC5B0BFC05B87FAD53889EB2E37BBF" }
      ]
    }
    """;

    private const string ContentsJson = """
    { "name": "motd.md", "path": "motd.md", "sha": "08EE5BBAFCFBA6AA5059A8CC9464B5ECF50BDB84", "size": 186 }
    """;

    [Fact]
    public void ParsesReleaseAssetUrl()
    {
        Assert.True(GitHubDigest.TryParseReleaseAsset(
            new Uri("https://github.com/clshortfuse/renodx/releases/download/snapshot/renodx-devkit.addon64"),
            out var asset));

        Assert.Equal("clshortfuse", asset.Owner);
        Assert.Equal("renodx", asset.Repo);
        Assert.Equal("snapshot", asset.Tag);
        Assert.Equal("renodx-devkit.addon64", asset.FileName);
    }

    [Fact]
    public void ParsesReleaseAssetUrl_WithEscapedFileName()
    {
        Assert.True(GitHubDigest.TryParseReleaseAsset(
            new Uri("https://github.com/o/r/releases/download/v1.2.3/My%20Mod%20v1.7z"),
            out var asset));

        Assert.Equal("v1.2.3", asset.Tag);
        Assert.Equal("My Mod v1.7z", asset.FileName);
    }

    [Theory]
    [InlineData("https://github.com/o/r")]
    [InlineData("https://github.com/o/r/archive/refs/heads/main.tar.gz")]
    [InlineData("https://raw.githubusercontent.com/o/r/main/file.dll")]
    [InlineData("https://api.github.com/repos/o/r/releases/tags/v1")]
    [InlineData("https://example.com/o/r/releases/download/v1/a.dll")]
    public void RejectsNonReleaseAssetUrls(string url) =>
        Assert.False(GitHubDigest.TryParseReleaseAsset(new Uri(url), out _));

    [Fact]
    public void ParsesRawFileUrl()
    {
        Assert.True(GitHubDigest.TryParseRawFile(
            new Uri("https://raw.githubusercontent.com/RankFTW/RHI/main/engine-files/foo/bar.ini"),
            out var file));

        Assert.Equal("RankFTW", file.Owner);
        Assert.Equal("RHI", file.Repo);
        Assert.Equal("main", file.Reference);
        Assert.Equal("engine-files/foo/bar.ini", file.Path);
    }

    [Fact]
    public void RejectsRawUrlWithoutFilePath() =>
        Assert.False(GitHubDigest.TryParseRawFile(
            new Uri("https://raw.githubusercontent.com/o/r/main"), out _));

    [Fact]
    public void BuildsApiUris()
    {
        Assert.Equal(
            "https://api.github.com/repos/o/r/releases/tags/v1",
            GitHubDigest.BuildReleaseApiUri(new ReleaseAssetRef("o", "r", "v1", "a.dll")).AbsoluteUri);

        Assert.Equal(
            "https://api.github.com/repos/o/r/contents/engine-files/a.ini?ref=main",
            GitHubDigest.BuildContentsApiUri(new RawFileRef("o", "r", "main", "engine-files/a.ini")).AbsoluteUri);
    }

    [Fact]
    public void ParsesAssetDigest_CaseInsensitively()
    {
        var sha = GitHubDigest.ParseReleaseAssetSha256(ReleaseJson, "renodx-devkit.addon64");
        Assert.Equal("788186855a4b733d32077eb5cc7248a04ecc5b0bfc05b87fad53889eb2e37bbf", sha);
    }

    [Fact]
    public void MissingAsset_HasNoDigest()
    {
        Assert.Null(GitHubDigest.ParseReleaseAssetSha256(ReleaseJson, "does-not-exist.dll"));
        Assert.Null(GitHubDigest.ParseReleaseAssetSize(ReleaseJson, "does-not-exist.dll"));
    }

    [Fact]
    public void ParsesAssetSize()
    {
        Assert.Equal(5454336, GitHubDigest.ParseReleaseAssetSize(ReleaseJson, "renodx-devkit.addon64"));
    }

    [Fact]
    public void AssetWithoutDigestField_ReturnsNull()
    {
        const string json = """
        { "assets": [ { "name": "old.exe", "size": 5 } ] }
        """;
        Assert.Null(GitHubDigest.ParseReleaseAssetSha256(json, "old.exe"));
        Assert.Equal(5, GitHubDigest.ParseReleaseAssetSize(json, "old.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void MalformedJson_DoesNotThrow(string json)
    {
        Assert.Null(GitHubDigest.ParseReleaseAssetSha256(json, "a.dll"));
        Assert.Null(GitHubDigest.ParseContentsBlobSha(json));
    }

    [Fact]
    public void ParsesContentsBlobSha()
    {
        Assert.Equal("08ee5bbafcfba6aa5059a8cc9464b5ecf50bdb84", GitHubDigest.ParseContentsBlobSha(ContentsJson));
    }

    [Fact]
    public void ShortContentsSha_IsRejected()
    {
        Assert.Null(GitHubDigest.ParseContentsBlobSha("{ \"sha\": \"abc\" }"));
    }

    [Fact]
    public void ComputesCanonicalGitBlobSha()
    {
        // echo -n test | git hash-object --stdin -> 30d74d258442c7c65512eafab474568dd706c430
        var hash = GitHubDigest.ComputeBlobSha1(Encoding.ASCII.GetBytes("test"));
        Assert.Equal("30d74d258442c7c65512eafab474568dd706c430", hash);
    }

    [Fact]
    public void BlobHeader_EncodesLength()
    {
        var header = Encoding.ASCII.GetString(GitHubDigest.BlobHeader(186));
        Assert.StartsWith("blob 186", header, StringComparison.Ordinal);
    }
}
