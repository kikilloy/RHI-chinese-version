// GitHubApiDiagnostics.cs — turns an api.github.com failure into one sentence a human can act on.
//
// The integrity check degrades to "unverified" when the digest lookup fails, and "unverified"
// on its own is useless: the user needs to know whether their token died, they hit the 60/hour
// anonymous limit, or GitHub is simply down. Pure functions so the wording is unit-tested.

using System.Text.Json;

namespace RenoDXCommander.Services.Net;

public static class GitHubApiDiagnostics
{
    /// <summary>How many characters of the API body are kept for diagnostics.</summary>
    private const int MaxBodyInspection = 400;

    /// <summary>
    /// One-line explanation of an API failure, including the fix when there is one.
    /// </summary>
    public static string Explain(int statusCode, string? body)
    {
        var message = ExtractMessage(body);

        if (statusCode == 401 || MentionsBadCredentials(message))
            return "GitHub token is invalid or was revoked — replace it in Settings > Accounts & Data > GitHub token";

        if (statusCode == 429 || MentionsRateLimit(message) || MentionsRateLimit(body))
            return "GitHub API rate limit reached (60/hour without a token) — add a token to raise it to 5000/hour";

        if (statusCode == 403 && MentionsFineGrainedDenial(message))
        {
            return "This fine-grained token (github_pat_…) is not allowed to read this repository — "
                 + "edit it and give it Public repositories (read-only) access, or use a classic token (ghp_…) with no scopes";
        }

        if (statusCode == 403)
            return "GitHub refused the request (token without access to this repository, or rate limited)";

        if (statusCode == 404)
            return "Release or repository not found (it may have been deleted or made private)";

        if (statusCode >= 500)
            return $"GitHub API is having trouble (HTTP {statusCode}) — try again later";

        return $"GitHub API returned HTTP {statusCode}";
    }

    /// <summary>Short form for the settings page.</summary>
    public static string ExplainBrief(int statusCode, string? body)
    {
        if (statusCode == 401 || MentionsBadCredentials(ExtractMessage(body)))
            return "GitHub token invalid";
        if (statusCode == 429 || MentionsRateLimit(ExtractMessage(body)) || MentionsRateLimit(body))
            return "GitHub rate limit";
        if (statusCode == 403 && MentionsFineGrainedDenial(ExtractMessage(body)))
            return "fine-grained token not allowed for this repo";
        return $"GitHub API HTTP {statusCode}";
    }

    /// <summary>Pulls the <c>message</c> field out of a GitHub error body (never throws).</summary>
    public static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy interstitial page, for example) — fall back to the raw text.
        }

        var trimmed = body.Trim();
        return trimmed.Length == 0
            ? null
            : trimmed[..Math.Min(trimmed.Length, MaxBodyInspection)];
    }

    private static bool MentionsBadCredentials(string? text) =>
        text is not null && text.Contains("Bad credentials", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fine-grained PATs (<c>github_pat_…</c>) must be told which repositories they may read; the
    /// API answers 403 with this exact message when they weren't. A classic no-scope token
    /// (<c>ghp_…</c>) reads public repositories without any permission setup, which is why the UI
    /// recommends it.
    /// </summary>
    private static bool MentionsFineGrainedDenial(string? text) =>
        text is not null
        && (text.Contains("Resource not accessible by personal access token", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Resource not accessible by integration", StringComparison.OrdinalIgnoreCase));

    private static bool MentionsRateLimit(string? text) =>
        text is not null
        && (text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("secondary rate", StringComparison.OrdinalIgnoreCase));
}
