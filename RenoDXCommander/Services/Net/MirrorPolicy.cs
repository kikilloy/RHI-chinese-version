// MirrorPolicy.cs — pure functions that decide which GitHub URLs may be sent through a
// China-friendly mirror prefix, and in which order to try them.
//
// This file deliberately reads no settings and performs no I/O: MirrorRewriteHandler supplies
// the options, and the unit tests can cover every branch without a network.

namespace RenoDXCommander.Services.Net;

/// <summary>How a single download attempt reaches GitHub.</summary>
public enum MirrorKind
{
    /// <summary>Original github.com / raw.githubusercontent.com URL.</summary>
    Direct = 0,
    /// <summary>A mirror prefix proxy: <c>{prefix}https://github.com/…</c>.</summary>
    Prefix = 1,
    /// <summary>jsDelivr's gh CDN, only usable for raw.githubusercontent.com.</summary>
    JsDelivr = 2,
}

/// <summary>One candidate URL for a download, in the order it should be tried.</summary>
public sealed record MirrorAttempt(MirrorKind Kind, Uri Uri, string? Prefix = null);

/// <summary>
/// Everything the policy needs. Passed in by the handler so this stays a pure function —
/// never read settings from here.
/// </summary>
public sealed record MirrorOptions(
    bool Enabled,
    IReadOnlyList<string> Prefixes,
    bool UseJsDelivrFallback = false)
{
    /// <summary>Mirroring disabled — every request goes straight to GitHub.</summary>
    public static MirrorOptions Direct { get; } =
        new(false, Array.Empty<string>(), false);
}

public static class MirrorPolicy
{
    /// <summary>
    /// Last-resort prefix list, used when the manifest cannot be fetched (or an old cached
    /// manifest has no <c>mirrorPrefixes</c>). Chosen by the measurements in
    /// <c>docs/mirror-eval.md</c>: fastest first, so the healthy one is tried first.
    /// </summary>
    public static IReadOnlyList<string> DefaultPrefixes { get; } = new[]
    {
        "https://gh.nxnow.top/",          // 5.2 MiB release asset in 33 s — the only mirror that finished
        "https://ghfile.geekertao.top/",  // ~46 KB/s, honest pass-through
        "https://gh-proxy.com/",          // slowest of the three, but the most faithful relay
    };

    /// <summary>
    /// Hosts owned by GitHub. Used only as a loop guard: a mirror prefix must not be one of
    /// these, and a rewritten URL must not end up back on one.
    /// </summary>
    private static readonly HashSet<string> GitHubOwnedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "www.github.com",
        "gist.github.com",
        "api.github.com",
        "uploads.github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "github.io",
        "www.github.io",
    };

    /// <summary>True when a URL is a GitHub download that a prefix mirror can be asked for.</summary>
    public static bool ShouldProxy(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;

        var host = uri.Host;
        if (string.Equals(host, "codeload.github.com", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return true;

        // Presigned release-asset URLs (objects.githubusercontent.com /
        // release-assets.githubusercontent.com). The prefix mirror already fetches these
        // server-side when asked for the github.com/releases/download URL, and the presigned
        // URL carries an expiry (se=…) that a proxy cannot renew — so never rewrite them.
        if (host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return false;

        if (string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var path = uri.AbsolutePath;
            // Release assets and source archives are the bulk of what RHI downloads.
            if (path.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.Contains("/archive/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        return false;
    }

    /// <summary>
    /// The ordered list of URLs to try for <paramref name="original"/>: every usable mirror,
    /// then the direct connection. Always contains at least the direct URL, so a mirror outage
    /// can never make a download impossible.
    /// </summary>
    public static IReadOnlyList<MirrorAttempt> BuildAttempts(Uri? original, MirrorOptions? options)
    {
        var attempts = new List<MirrorAttempt>();
        if (original is null || !original.IsAbsoluteUri) return attempts;

        var direct = new MirrorAttempt(MirrorKind.Direct, original);

        if (options is not { Enabled: true } || !ShouldProxy(original))
        {
            attempts.Add(direct);
            return attempts;
        }

        // Idempotence: a URL that already points at a mirror host must not be wrapped again.
        if (IsAlreadyProxied(original, options.Prefixes))
        {
            attempts.Add(direct);
            return attempts;
        }

        foreach (var prefix in options.Prefixes)
        {
            if (TryBuildPrefixed(original, prefix, out var proxied)
                && TryNormalizePrefix(prefix, out var normalized))
            {
                attempts.Add(new MirrorAttempt(MirrorKind.Prefix, proxied, normalized));
            }
        }

        if (options.UseJsDelivrFallback && TryBuildJsDelivr(original, out var jsdelivr))
            attempts.Add(new MirrorAttempt(MirrorKind.JsDelivr, jsdelivr));

        attempts.Add(direct);
        return attempts;
    }

    /// <summary>
    /// First choice for <paramref name="original"/> when mirroring is on; the original URL when
    /// nothing applies. Convenience wrapper around <see cref="BuildAttempts"/>.
    /// </summary>
    public static bool TryRewrite(Uri? original, MirrorOptions? options, out Uri? rewritten, out MirrorKind kind)
    {
        var attempts = BuildAttempts(original, options);
        rewritten = attempts.Count > 0 ? attempts[0].Uri : original;
        kind = attempts.Count > 0 ? attempts[0].Kind : MirrorKind.Direct;
        return attempts.Count > 0 && attempts[0].Kind != MirrorKind.Direct;
    }

    /// <summary>
    /// <c>{prefix}https://github.com/…</c>. The prefix must be HTTPS and must not itself be a
    /// GitHub host (that would be a rewrite loop).
    /// </summary>
    public static bool TryBuildPrefixed(Uri? original, string? prefix, out Uri proxied)
    {
        proxied = null!;
        if (original is null || !original.IsAbsoluteUri) return false;
        if (!TryNormalizePrefix(prefix, out var normalized)) return false;

        var target = normalized + original.AbsoluteUri;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var candidate)) return false;
        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsGitHubHost(candidate.Host)) return false;   // loop guard
        proxied = candidate;
        return true;
    }

    /// <summary>
    /// <c>raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}</c> →
    /// <c>https://cdn.jsdelivr.net/gh/{owner}/{repo}@{ref}/{path}</c>.
    /// Only raw URLs can be mapped this way. Disabled by default: jsDelivr edge-caches for 12 h,
    /// which would serve a stale manifest (see docs/mirror-eval.md §6).
    /// </summary>
    public static bool TryBuildJsDelivr(Uri? original, out Uri jsdelivr)
    {
        jsdelivr = null!;
        if (original is null || !original.IsAbsoluteUri) return false;
        if (!string.Equals(original.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = original.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 4) return false;

        var owner = segments[0];
        var repo = segments[1];
        var reference = segments[2];
        var path = string.Join('/', segments.Skip(3));
        if (owner.Length == 0 || repo.Length == 0 || reference.Length == 0 || path.Length == 0) return false;

        return Uri.TryCreate(
            $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{reference}/{path}",
            UriKind.Absolute,
            out jsdelivr);
    }

    /// <summary>True when <paramref name="uri"/> already points at one of the configured mirrors.</summary>
    public static bool IsAlreadyProxied(Uri? uri, IReadOnlyList<string>? prefixes)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        var absolute = uri.AbsoluteUri;

        foreach (var prefix in prefixes ?? Array.Empty<string>())
        {
            if (!TryNormalizePrefix(prefix, out var normalized)) continue;
            if (absolute.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)) return true;
            // Same host as the mirror but without the scheme/leading slash prefix we expect.
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var prefixUri)
                && string.Equals(uri.Host, prefixUri.Host, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Trims, validates and trailing-slash-normalises a configured prefix.</summary>
    public static bool TryNormalizePrefix(string? prefix, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(prefix)) return false;

        var trimmed = prefix.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (IsGitHubHost(uri.Host)) return false;   // a GitHub host as prefix would be a no-op or a loop

        normalized = trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
        return true;
    }

    /// <summary>Host of a prefix, for logging. Empty when the prefix is unusable.</summary>
    public static string PrefixHost(string? prefix) =>
        TryNormalizePrefix(prefix, out var normalized)
        && Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;

    /// <summary>True for any github.com-owned host (used for loop guards, not for policy).</summary>
    public static bool IsGitHubHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (GitHubOwnedHosts.Contains(host)) return true;
        return host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase);
    }
}
