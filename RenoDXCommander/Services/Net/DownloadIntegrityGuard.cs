// DownloadIntegrityGuard.cs — asks GitHub what a mirrored download *should* hash to.
//
// A mirror is a third party. GitHub's own API publishes the authoritative hash for every release
// asset (assets[].digest) and every repository file (contents API blob sha), and api.github.com is
// never proxied — so the comparison is meaningful even when the bytes came through a mirror.
//
// Degradation is deliberate: no cached answer, no API answer, no rate limit left, or a file too
// large to blob-hash → return null and let the download continue. Blocking installs when GitHub
// is unreachable would be worse than the risk we're guarding against (see ADR-0002).

using System.Collections.Concurrent;

namespace RenoDXCommander.Services.Net;

public interface IDownloadIntegrityGuard
{
    /// <summary>
    /// The hash <paramref name="originalUri"/>'s bytes must produce, or null when it cannot be
    /// determined (verification off, no digest published, API unreachable, unsupported shape).
    /// </summary>
    Task<DigestExpectation?> GetExpectationAsync(
        Uri originalUri,
        long? contentLength,
        Func<Uri, CancellationToken, Task<string?>> fetchApiJson,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class DownloadIntegrityGuard : IDownloadIntegrityGuard
{
    /// <summary>Git blob shas need the length up front; huge files are left unverified.</summary>
    private const long MaxBlobVerifiedLength = 32L * 1024 * 1024;

    private readonly GitHubDigestCache _cache;

    public DownloadIntegrityGuard() : this(new GitHubDigestCache()) { }

    /// <summary>Test seam.</summary>
    public DownloadIntegrityGuard(GitHubDigestCache cache) => _cache = cache;

    public async Task<DigestExpectation?> GetExpectationAsync(
        Uri originalUri,
        long? contentLength,
        Func<Uri, CancellationToken, Task<string?>> fetchApiJson,
        CancellationToken cancellationToken)
    {
        if (originalUri is null || fetchApiJson is null) return null;
        if (!MirrorRuntime.VerifyDownloads) return null;

        if (GitHubDigest.TryParseReleaseAsset(originalUri, out var asset))
        {
            var cached = _cache.TryGet(asset.CacheKey);
            if (cached is not null) return DigestExpectation.Sha256(cached, contentLength);

            var json = await fetchApiJson(GitHubDigest.BuildReleaseApiUri(asset), cancellationToken)
                .ConfigureAwait(false);

            // null means the API call itself failed; the caller already reported why.
            if (json is null) return null;

            var sha = GitHubDigest.ParseReleaseAssetSha256(json, asset.FileName);

            if (sha is null || !GitHubDigest.LooksLikeHex(sha, 64))
            {
                CrashReporter.Log($"[DownloadIntegrityGuard] No digest for {asset.Owner}/{asset.Repo}@{asset.Tag}/{asset.FileName} — verification skipped");
                MirrorRuntime.ReportIntegritySkip($"GitHub publishes no sha256 digest for {asset.FileName}");
                return null;
            }

            _cache.Set(asset.CacheKey, sha);
            var size = GitHubDigest.ParseReleaseAssetSize(json, asset.FileName);
            return DigestExpectation.Sha256(sha, size ?? contentLength);
        }

        if (GitHubDigest.TryParseRawFile(originalUri, out var raw))
        {
            if (contentLength is null or < 0)
            {
                CrashReporter.Log($"[DownloadIntegrityGuard] Unknown length for {raw.Owner}/{raw.Repo}@{raw.Reference}/{raw.Path} — blob verification skipped");
                return null;
            }

            if (contentLength > MaxBlobVerifiedLength)
            {
                CrashReporter.Log($"[DownloadIntegrityGuard] {contentLength} bytes is too large for blob-sha verification ({raw.Owner}/{raw.Repo}/{raw.Path}) — skipped");
                return null;
            }

            var cached = _cache.TryGet(raw.CacheKey);
            if (cached is not null) return DigestExpectation.GitBlobSha1(cached, contentLength.Value, contentLength.Value);

            var json = await fetchApiJson(GitHubDigest.BuildContentsApiUri(raw), cancellationToken)
                .ConfigureAwait(false);
            if (json is null) return null;   // API failure — already reported

            var sha = GitHubDigest.ParseContentsBlobSha(json);

            if (sha is null)
            {
                CrashReporter.Log($"[DownloadIntegrityGuard] No blob sha for {raw.Owner}/{raw.Repo}@{raw.Reference}/{raw.Path} — verification skipped");
                MirrorRuntime.ReportIntegritySkip($"GitHub publishes no blob sha for {raw.Path}");
                return null;
            }

            _cache.Set(raw.CacheKey, sha);
            return DigestExpectation.GitBlobSha1(sha, contentLength.Value, contentLength.Value);
        }

        return null;
    }
}

/// <summary>
/// 24 h in-memory + on-disk cache of asset → hash. Without it, verifying a few components would
/// burn the unauthenticated 60 requests/hour GitHub API budget and start failing installs.
/// </summary>
public sealed class GitHubDigestCache
{
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RHI", "github_digest_cache.json");

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, SavedEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _writeGate = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly bool _persist;

    public GitHubDigestCache() : this(() => DateTimeOffset.UtcNow) { }

    public GitHubDigestCache(Func<DateTimeOffset> now) : this(now, loadFromDisk: true) { }

    /// <param name="loadFromDisk">False for an in-memory-only cache (tests).</param>
    /// <param name="persist">False to also skip writing (tests).</param>
    public GitHubDigestCache(Func<DateTimeOffset> now, bool loadFromDisk, bool persist = true)
    {
        _now = now;
        _persist = persist;
        if (loadFromDisk) Load();
    }

    /// <summary>Persisted shape of one entry.</summary>
    public sealed class SavedEntry
    {
        public string Sha { get; set; } = "";
        public DateTime SavedUtc { get; set; }
    }

    public string? TryGet(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        if (!_entries.TryGetValue(key, out var entry)) return null;

        if (_now().UtcDateTime - entry.SavedUtc > MaxAge)
        {
            _entries.TryRemove(key, out _);
            return null;
        }
        return string.IsNullOrWhiteSpace(entry.Sha) ? null : entry.Sha;
    }

    public void Set(string key, string sha)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrWhiteSpace(sha)) return;
        _entries[key] = new SavedEntry { Sha = sha, SavedUtc = _now().UtcDateTime };
        Save();
    }

    public int Count => _entries.Count;

    private void Load()
    {
        try
        {
            var saved = SafeJsonFile.Read<Dictionary<string, SavedEntry>>(CachePath, out _);
            if (saved is null) return;

            var cutoff = _now().UtcDateTime - MaxAge;
            foreach (var kv in saved)
            {
                if (kv.Value is null || string.IsNullOrWhiteSpace(kv.Value.Sha)) continue;
                if (kv.Value.SavedUtc < cutoff) continue;
                _entries[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Log($"[GitHubDigestCache.Load] Failed — {ex.Message}");
        }
    }

    private void Save()
    {
        if (!_persist) return;

        lock (_writeGate)
        {
            try
            {
                var snapshot = _entries.ToDictionary(kv => kv.Key, kv => kv.Value);
                SafeJsonFile.Write(CachePath, snapshot, "[GitHubDigestCache.Save]");
            }
            catch (Exception ex)
            {
                CrashReporter.Log($"[GitHubDigestCache.Save] Failed — {ex.Message}");
            }
        }
    }
}
