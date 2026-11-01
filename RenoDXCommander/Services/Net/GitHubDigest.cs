// GitHubDigest.cs — pure helpers for integrity verification of mirrored downloads.
//
// Everything here is string/URI work with no I/O, so the dangerous part (deciding whether a
// downloaded DLL is the one GitHub published) can be unit-tested offline.
//
// Data sources (see ADR-0002):
//   release assets → GET api.github.com/repos/{o}/{r}/releases/tags/{tag} → assets[].digest ("sha256:…")
//   raw files      → GET api.github.com/repos/{o}/{r}/contents/{path}?ref={ref} → sha (git blob sha1)
// api.github.com is NEVER proxied, so a mirror cannot influence these answers.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RenoDXCommander.Services.Net;

/// <summary>Identifies a GitHub release asset: enough to look up its published digest.</summary>
public sealed record ReleaseAssetRef(string Owner, string Repo, string Tag, string FileName)
{
    public string CacheKey => $"rel:{Owner}/{Repo}@{Tag}/{FileName}".ToLowerInvariant();
}

/// <summary>Identifies a file inside a GitHub repository, addressed by ref + path.</summary>
public sealed record RawFileRef(string Owner, string Repo, string Reference, string Path)
{
    public string CacheKey => $"raw:{Owner}/{Repo}@{Reference}/{Path}".ToLowerInvariant();
}

/// <summary>Which hash a verification compares against.</summary>
public enum IntegrityAlgorithm
{
    /// <summary>Plain SHA-256 of the file bytes (GitHub release asset digest).</summary>
    Sha256 = 0,
    /// <summary>Git blob id: SHA-1 over "blob {length}\0" + bytes.</summary>
    GitBlobSha1 = 1,
}

/// <summary>What the downloaded bytes are expected to hash to.</summary>
public sealed record DigestExpectation(
    IntegrityAlgorithm Algorithm,
    string ExpectedHex,
    long BlobLength,
    long? ExpectedLength = null)
{
    public static DigestExpectation Sha256(string expectedHex, long? expectedLength = null) =>
        new(IntegrityAlgorithm.Sha256, expectedHex.Trim().ToLowerInvariant(), -1, expectedLength);

    public static DigestExpectation GitBlobSha1(string expectedHex, long blobLength, long? expectedLength = null) =>
        new(IntegrityAlgorithm.GitBlobSha1, expectedHex.Trim().ToLowerInvariant(), blobLength,
            expectedLength ?? (blobLength >= 0 ? blobLength : null));
}

public static class GitHubDigest
{
    private const string ApiRoot = "https://api.github.com";
    private const string ReleaseMarker = "/releases/download/";

    /// <summary>
    /// <c>https://github.com/{owner}/{repo}/releases/download/{tag}/{file}</c>.
    /// Tags may contain slashes in theory, but GitHub rejects them for release tags, so the first
    /// segment after <c>/releases/download/</c> is the tag and the rest is the file name.
    /// </summary>
    public static bool TryParseReleaseAsset(Uri? uri, out ReleaseAssetRef asset)
    {
        asset = null!;
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "www.github.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.AbsolutePath;
        var markerIndex = path.IndexOf(ReleaseMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 1) return false;

        var repoPart = path[..markerIndex].Trim('/').Split('/');
        if (repoPart.Length != 2) return false;

        var tail = path[(markerIndex + ReleaseMarker.Length)..].Trim('/').Split('/');
        if (tail.Length < 2) return false;

        var owner = Uri.UnescapeDataString(repoPart[0]);
        var repo = Uri.UnescapeDataString(repoPart[1]);
        var tag = Uri.UnescapeDataString(tail[0]);
        var fileName = Uri.UnescapeDataString(string.Join('/', tail.Skip(1)));

        if (owner.Length == 0 || repo.Length == 0 || tag.Length == 0 || fileName.Length == 0) return false;

        asset = new ReleaseAssetRef(owner, repo, tag, fileName);
        return true;
    }

    /// <summary><c>https://raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}</c>.</summary>
    public static bool TryParseRawFile(Uri? uri, out RawFileRef file)
    {
        file = null!;
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 4) return false;

        var owner = Uri.UnescapeDataString(segments[0]);
        var repo = Uri.UnescapeDataString(segments[1]);
        var reference = Uri.UnescapeDataString(segments[2]);
        var path = Uri.UnescapeDataString(string.Join('/', segments.Skip(3)));

        if (owner.Length == 0 || repo.Length == 0 || reference.Length == 0 || path.Length == 0) return false;

        file = new RawFileRef(owner, repo, reference, path);
        return true;
    }

    /// <summary>API endpoint that lists the release's assets, including their digests.</summary>
    public static Uri BuildReleaseApiUri(ReleaseAssetRef asset) =>
        new($"{ApiRoot}/repos/{Escape(asset.Owner)}/{Escape(asset.Repo)}/releases/tags/{Escape(asset.Tag)}");

    /// <summary>API endpoint that returns the blob sha of a repository file.</summary>
    public static Uri BuildContentsApiUri(RawFileRef file) =>
        new($"{ApiRoot}/repos/{Escape(file.Owner)}/{Escape(file.Repo)}/contents/{EscapePath(file.Path)}?ref={Escape(file.Reference)}");

    /// <summary>
    /// Reads <c>assets[].digest</c> for one asset. Returns null when the asset is absent or the
    /// release predates GitHub's digest field — callers must degrade, never block.
    /// </summary>
    public static string? ParseReleaseAssetSha256(string? json, string assetName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(assetName)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    continue;
                if (!string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (asset.TryGetProperty("digest", out var digest) && digest.ValueKind == JsonValueKind.String)
                {
                    var value = digest.GetString();
                    const string prefix = "sha256:";
                    if (value is not null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return value[prefix.Length..].Trim().ToLowerInvariant();
                }

                return null;   // found the asset, but it has no digest
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads <c>assets[].size</c> for one asset. A mirror answering with an HTML interstitial page
    /// is by far the most common failure, and the size lets us reject it before one byte of the
    /// body is streamed into a game folder.
    /// </summary>
    public static long? ParseReleaseAssetSize(string? json, string assetName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(assetName)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;
                if (!asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    continue;
                if (!string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var value) && value >= 0)
                    return value;
                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>Reads the git blob sha1 from a contents API response.</summary>
    public static string? ParseContentsBlobSha(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String) return null;

            var value = sha.GetString();
            if (string.IsNullOrWhiteSpace(value) || value.Length != 40) return null;
            return value.Trim().ToLowerInvariant();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The git blob object id of an in-memory buffer: sha1("blob {len}\0" + content).</summary>
    public static string ComputeBlobSha1(ReadOnlySpan<byte> content)
    {
        var header = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha1 = SHA1.Create();
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        sha1.TransformFinalBlock(content.ToArray(), 0, content.Length);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    /// <summary>The <c>blob {len}\0</c> prefix a streaming blob-sha1 computation must hash first.</summary>
    public static byte[] BlobHeader(long length) => Encoding.ASCII.GetBytes($"blob {length}\0");

    /// <summary>True when a hex string looks like a hash we could compare against.</summary>
    public static bool LooksLikeHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
