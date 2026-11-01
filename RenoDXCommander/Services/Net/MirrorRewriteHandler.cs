// MirrorRewriteHandler.cs — sends GitHub downloads through mirror prefixes, with fallback.
//
// Sits OUTSIDE GitHubTokenHandler in the shared HttpClient pipeline:
//     HttpClient → MirrorRewriteHandler → GitHubTokenHandler → SocketsHttpHandler
// so a mirrored request is rewritten before the token handler decides whether the (now
// non-GitHub) host may see the token — which is never.
//
// Only GET/HEAD are retried: other methods may carry a body that cannot be replayed.

using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace RenoDXCommander.Services.Net;

public sealed class MirrorRewriteHandler : DelegatingHandler
{
    /// <summary>
    /// How long a mirror gets to answer with response headers. Deliberately NOT a whole-request
    /// timeout: the shared HttpClient already allows 10 minutes, and a 5 GB asset on a slow link
    /// must not be killed. Once headers arrive the clock stops (see SendWithFirstByteTimeoutAsync).
    /// </summary>
    public static TimeSpan DefaultFirstByteTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Budget for the "what should this hash to?" API call. Never blocks an install.</summary>
    private static readonly TimeSpan ApiLookupTimeout = TimeSpan.FromSeconds(8);

    private readonly IMirrorHealthTracker _health;
    private readonly Func<MirrorOptions> _options;
    private readonly TimeSpan _firstByteTimeout;
    private readonly HttpClient _mirrorClient;
    private readonly IDownloadIntegrityGuard _integrity;

    /// <param name="firstByteTimeout">Override for tests; production uses 15 s.</param>
    /// <param name="mirrorClient">
    /// Client used for the mirrored hop. Defaults to a dedicated one (see CreateMirrorClient) —
    /// it must be separate from the shared HttpClient, because DelegatingHandler cannot pass
    /// HttpCompletionOption down its chain and we need ResponseHeadersRead to keep the 15 s
    /// budget on the first byte instead of the whole body.
    /// </param>
    public MirrorRewriteHandler(
        HttpMessageHandler innerHandler,
        IMirrorHealthTracker? health = null,
        Func<MirrorOptions>? options = null,
        TimeSpan? firstByteTimeout = null,
        HttpClient? mirrorClient = null,
        IDownloadIntegrityGuard? integrity = null)
        : base(innerHandler)
    {
        _health = health ?? new MirrorHealthTracker();
        _options = options ?? (() => MirrorRuntime.Options);
        _firstByteTimeout = firstByteTimeout ?? DefaultFirstByteTimeout;
        _mirrorClient = mirrorClient ?? CreateMirrorClient();
        _integrity = integrity ?? new DownloadIntegrityGuard();
    }

    /// <summary>
    /// Dedicated client for third-party mirrors: no cookie jar sharing, HTTP/1.1 (many mirrors
    /// don't speak/route HTTP/2 correctly), and a 10 s connect timeout so an unreachable mirror
    /// fails fast even before the first-byte budget starts.
    /// </summary>
    private static HttpClient CreateMirrorClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Add("User-Agent", "RHI/2.0");
        client.DefaultRequestVersion = HttpVersion.Version11;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return client;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var options = _options();
        var original = request.RequestUri;

        if (!options.Enabled
            || original is null
            || (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
            || !MirrorPolicy.ShouldProxy(original))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var attempts = MirrorPolicy.BuildAttempts(original, options);
        if (attempts.Count <= 1)
        {
            // Nothing proxyable (or no usable prefix) — plain request, no extra work.
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        string? lastFailure = null;

        foreach (var attempt in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt.Kind == MirrorKind.Direct)
            {
                if (lastFailure is not null)
                {
                    CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=fallback-to-direct reason={lastFailure}");
                    MirrorRuntime.NotifyFellBackToDirect(lastFailure);
                }
                // Direct is the last resort and must surface its own failures untouched.
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var mirrorKey = attempt.Prefix ?? attempt.Uri.Host;

            if (_health.IsCircuitOpen(mirrorKey))
            {
                lastFailure ??= mirrorKey + ": circuit open";
                CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=skip prefix={mirrorKey} reason=circuit-open failures={_health.ConsecutiveFailures(mirrorKey)}");
                continue;
            }

            HttpRequestMessage? mirroredRequest = null;
            try
            {
                mirroredRequest = CloneForMirror(request, attempt.Uri);
                var response = await SendWithFirstByteTimeoutAsync(mirroredRequest, cancellationToken)
                    .ConfigureAwait(false);

                if (IsUsable(response))
                {
                    var expectation = await TryGetExpectationAsync(original, response, cancellationToken)
                        .ConfigureAwait(false);

                    // Cheap pre-filter: an interstitial/error page has the wrong length, so reject
                    // it here instead of streaming garbage into a game folder.
                    if (expectation?.ExpectedLength is long expectedLength
                        && response.Content.Headers.ContentLength is long actualLength
                        && actualLength != expectedLength)
                    {
                        _health.RecordFailure(mirrorKey);
                        lastFailure = $"{mirrorKey}: size {actualLength} != {expectedLength}";
                        CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=fail kind={attempt.Kind} host={attempt.Uri.Host} reason=size-mismatch actual={actualLength} expected={expectedLength} url={Shorten(original.AbsoluteUri)}");
                        response.Dispose();
                        continue;
                    }

                    if (expectation is not null)
                    {
                        var failedMirrorKey = mirrorKey;
                        response.Content = new VerifyingHttpContent(
                            response.Content,
                            expectation,
                            _ => _health.RecordFailure(failedMirrorKey));
                        CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] verify={expectation.Algorithm} expected={expectation.ExpectedHex} host={attempt.Uri.Host}");
                    }

                    _health.RecordSuccess(mirrorKey);
                    CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=ok kind={attempt.Kind} host={attempt.Uri.Host} status={(int)response.StatusCode} url={Shorten(original.AbsoluteUri)}");
                    return response;
                }

                _health.RecordFailure(mirrorKey);
                lastFailure = $"{mirrorKey}: HTTP {(int)response.StatusCode}";
                CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=fail kind={attempt.Kind} host={attempt.Uri.Host} status={(int)response.StatusCode} url={Shorten(original.AbsoluteUri)}");
                response.Dispose();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own 15 s first-byte budget expired; the caller did not cancel.
                _health.RecordFailure(mirrorKey);
                lastFailure = $"{mirrorKey}: first-byte timeout";
                CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=fail kind={attempt.Kind} host={attempt.Uri.Host} reason=first-byte-timeout({_firstByteTimeout.TotalSeconds}s) url={Shorten(original.AbsoluteUri)}");
            }
            catch (HttpRequestException ex)
            {
                _health.RecordFailure(mirrorKey);
                lastFailure = $"{mirrorKey}: {ex.Message}";
                CrashReporter.Log($"[MirrorRewriteHandler.SendAsync] result=fail kind={attempt.Kind} host={attempt.Uri.Host} reason={ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                mirroredRequest?.Dispose();
            }
        }

        // Unreachable in practice (Direct always ends the loop), but keep a deterministic tail.
        throw new HttpRequestException($"[Mirror] all download attempts failed: {lastFailure ?? "unknown"}");
    }

    /// <summary>
    /// Sends with a first-byte (response headers) budget instead of a whole-request one.
    /// ResponseHeadersRead is used so "headers arrived" really means the body hasn't streamed yet.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithFirstByteTimeoutAsync(
        HttpRequestMessage message,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_firstByteTimeout);
        try
        {
            var response = await _mirrorClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            // Headers are in: stop the clock. The body may legitimately take minutes.
            timeoutCts.CancelAfter(Timeout.InfiniteTimeSpan);

            // Intentionally NOT disposed: the response's content stream is still linked to this
            // token, and cancelling/disposing it mid-download would truncate the file. It is
            // collected with the response; the only cost is one registration on the caller's token.
            return response;
        }
        catch
        {
            timeoutCts.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks the integrity guard what the mirrored bytes must hash to. Every failure path returns
    /// null: an unavailable answer degrades to "unverified", never to "installation blocked".
    /// </summary>
    private async Task<DigestExpectation?> TryGetExpectationAsync(
        Uri original,
        HttpResponseMessage mirrored,
        CancellationToken cancellationToken)
    {
        if (!MirrorRuntime.VerifyDownloads) return null;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ApiLookupTimeout);

            return await _integrity
                .GetExpectationAsync(original, mirrored.Content.Headers.ContentLength, FetchApiJsonAsync, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CrashReporter.Log("[MirrorRewriteHandler.TryGetExpectationAsync] Digest lookup timed out — download continues unverified");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            CrashReporter.Log($"[MirrorRewriteHandler.TryGetExpectationAsync] Digest lookup failed — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetches a small api.github.com JSON document through this same pipeline. api.github.com is
    /// never proxied by <see cref="MirrorPolicy"/>, so this re-entry takes the plain path and still
    /// receives the token attached by GitHubTokenHandler.
    /// </summary>
    private async Task<string?> FetchApiJsonAsync(Uri apiUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            CrashReporter.Log($"[MirrorRewriteHandler.FetchApiJsonAsync] HTTP {(int)response.StatusCode} for {apiUri.Host}{apiUri.AbsolutePath}");
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>2xx, or anything a caller might legitimately be waiting for (304 / unfollowed 3xx).</summary>
    private static bool IsUsable(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return true;
        var code = (int)response.StatusCode;
        return code == 304 || (code >= 300 && code < 400);
    }

    /// <summary>
    /// A fresh request for the mirror target. Authorization is dropped on purpose: a
    /// third-party mirror must never see the user's GitHub token, and api.github.com — the
    /// only host the token is for — is never mirrored in the first place.
    /// </summary>
    private static HttpRequestMessage CloneForMirror(HttpRequestMessage source, Uri uri)
    {
        var clone = new HttpRequestMessage(source.Method, uri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy,
        };

        foreach (var header in source.Headers)
        {
            if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // No content copying: only GET/HEAD are mirrored.
        return clone;
    }

    private static string Shorten(string value) =>
        value.Length <= 140 ? value : value[..137] + "...";
}
