using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// The mirror pipeline: rewrite → try mirrors in order → fall back to direct, with a session
/// circuit breaker. These tests drive the real handler with a fake inner handler, so ordering,
/// the 15 s budget and the "never retry a POST" rule are covered without a network.
/// </summary>
public class MirrorRewriteHandlerTests : IDisposable
{
    private static readonly string[] OneMirror = { "https://m1.example/" };

    private const string AssetUrl =
        "https://github.com/o/r/releases/download/v1/a.dll";

    public void Dispose() => MirrorRuntime.Reset();

    private static MirrorOptions Enabled(params string[] prefixes) =>
        new(true, prefixes.Length == 0 ? OneMirror : prefixes);

    /// <summary>Short first-byte budget so timeout paths run in milliseconds, not 15 s.</summary>
    private static readonly TimeSpan TestFirstByteTimeout = TimeSpan.FromMilliseconds(150);

    private static MirrorRewriteHandler Build(
        FakeHandler inner,
        IMirrorHealthTracker health,
        MirrorOptions options,
        IDownloadIntegrityGuard? integrity = null) =>
        // Mirrored hops go through their own client (production: a dedicated one); point it at the
        // same fake so a single Urls list records both mirrored and direct attempts.
        new(inner, health, () => options, TestFirstByteTimeout,
            new HttpClient(inner, disposeHandler: false),
            integrity ?? new SilentIntegrityGuard());

    [Fact]
    public async Task Disabled_SendsOriginalUrlOnce()
    {
        var inner = new FakeHandler(_ => Ok());
        using var handler = Build(inner, new MirrorHealthTracker(), MirrorOptions.Direct);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(AssetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { AssetUrl }, inner.Urls);
    }

    [Fact]
    public async Task Enabled_MirrorSucceeds_OnlyMirrorIsCalled()
    {
        var inner = new FakeHandler(_ => Ok());
        using var handler = Build(inner, new MirrorHealthTracker(), Enabled());
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(AssetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(inner.Urls);
        Assert.StartsWith("https://m1.example/", inner.Urls[0]);
    }

    [Fact]
    public async Task MirrorFails_FallsBackToNextMirror()
    {
        var inner = new FakeHandler(req => req.RequestUri!.Host == "m1.example" ? Status(HttpStatusCode.BadGateway) : Ok());
        var options = Enabled("https://m1.example/", "https://m2.example/");
        using var handler = Build(inner, new MirrorHealthTracker(), options);
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(AssetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Urls.Count);
        Assert.StartsWith("https://m1.example/", inner.Urls[0]);
        Assert.StartsWith("https://m2.example/", inner.Urls[1]);
    }

    [Fact]
    public async Task AllMirrorsFail_FallsBackToDirectAndRaisesNotice()
    {
        var inner = new FakeHandler(req => req.RequestUri!.Host == "github.com" ? Ok() : Status(HttpStatusCode.BadGateway));
        var options = Enabled("https://m1.example/", "https://m2.example/");
        using var handler = Build(inner, new MirrorHealthTracker(), options);
        using var client = new HttpClient(handler);

        var notices = new List<string>();
        void OnFallback(string reason) => notices.Add(reason);
        MirrorRuntime.FellBackToDirect += OnFallback;
        try
        {
            using var response = await client.GetAsync(AssetUrl);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            MirrorRuntime.FellBackToDirect -= OnFallback;
        }

        Assert.Equal(3, inner.Urls.Count);
        Assert.Equal(AssetUrl, inner.Urls[2]);
        Assert.Single(notices);
    }

    [Fact]
    public async Task MirrorTimeout_FallsBackToDirect()
    {
        // Simulates the 15 s first-byte budget expiring: the inner handler stalls until cancelled.
        var inner = new FakeHandler(async (req, ct) =>
        {
            if (req.RequestUri!.Host == "m1.example")
                await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });
        using var handler = Build(inner, new MirrorHealthTracker(), Enabled());
        using var client = new HttpClient(handler);

        using var response = await client.GetAsync(AssetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Urls.Count);
        Assert.Equal(AssetUrl, inner.Urls[1]);
    }

    [Fact]
    public async Task TwoConsecutiveFailures_TripsBreaker_AndSkipsMirror()
    {
        var health = new MirrorHealthTracker();
        var inner = new FakeHandler(req => req.RequestUri!.Host == "m1.example" ? Status(HttpStatusCode.BadGateway) : Ok());
        using var client = new HttpClient(Build(inner, health, Enabled()));

        // Request 1 and 2 each fail once on the mirror → threshold reached.
        (await client.GetAsync(AssetUrl)).Dispose();
        (await client.GetAsync(AssetUrl)).Dispose();
        Assert.True(health.IsCircuitOpen("https://m1.example/"));

        inner.Urls.Clear();
        (await client.GetAsync(AssetUrl)).Dispose();

        // Third request goes straight to github.com — the dead mirror costs nothing.
        Assert.Equal(new[] { AssetUrl }, inner.Urls);
    }

    [Fact]
    public async Task MirrorSuccess_ResetsFailureCount()
    {
        var health = new MirrorHealthTracker();
        var fail = true;
        var inner = new FakeHandler(req =>
            req.RequestUri!.Host == "m1.example" && fail ? Status(HttpStatusCode.BadGateway) : Ok());
        using var client = new HttpClient(Build(inner, health, Enabled()));

        (await client.GetAsync(AssetUrl)).Dispose();
        Assert.Equal(1, health.ConsecutiveFailures("https://m1.example/"));

        fail = false;
        (await client.GetAsync(AssetUrl)).Dispose();
        Assert.Equal(0, health.ConsecutiveFailures("https://m1.example/"));
        Assert.False(health.IsCircuitOpen("https://m1.example/"));
    }

    [Fact]
    public async Task PostIsNeverMirrored()
    {
        var inner = new FakeHandler(_ => Ok());
        using var handler = Build(inner, new MirrorHealthTracker(), Enabled());
        using var client = new HttpClient(handler);

        using var response = await client.PostAsync(AssetUrl, new StringContent("x"));

        Assert.Single(inner.Urls);
        Assert.Equal(AssetUrl, inner.Urls[0]);
    }

    [Fact]
    public async Task ApiGitHubIsNeverMirrored()
    {
        var inner = new FakeHandler(_ => Ok());
        using var handler = Build(inner, new MirrorHealthTracker(), Enabled());
        using var client = new HttpClient(handler);

        var api = "https://api.github.com/repos/o/r/releases/tags/v1";
        using var response = await client.GetAsync(api);

        Assert.Equal(new[] { api }, inner.Urls);
    }

    [Fact]
    public async Task NonGitHubHostIsNeverMirrored()
    {
        var inner = new FakeHandler(_ => Ok());
        using var handler = Build(inner, new MirrorHealthTracker(), Enabled());
        using var client = new HttpClient(handler);

        var nexus = "https://www.nexusmods.com/site/mods/1";
        using var response = await client.GetAsync(nexus);

        Assert.Equal(new[] { nexus }, inner.Urls);
    }

    [Fact]
    public async Task AuthorizationHeader_IsNotForwardedToMirror()
    {
        var seen = new List<string?>();
        var handler = new MirrorRewriteHandler(
            new FakeHandler(_ => Ok()),
            new MirrorHealthTracker(),
            () => Enabled(),
            TestFirstByteTimeout,
            new HttpClient(new FakeHandler(req =>
            {
                seen.Add(req.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null);
                return Ok();
            }), disposeHandler: false),
            new SilentIntegrityGuard());
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Get, AssetUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(seen);
        Assert.Null(seen[0]);   // mirror must never see the token
    }

    [Fact]
    public async Task HeadRequests_AreMirrored()
    {
        var inner = new FakeHandler(_ => Ok());
        using var handler = Build(inner, new MirrorHealthTracker(), Enabled());
        using var client = new HttpClient(handler);

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, AssetUrl));

        Assert.Single(inner.Urls);
        Assert.StartsWith("https://m1.example/", inner.Urls[0]);
    }

    [Fact]
    public async Task MirrorReturning404_DoesNotCountAsSuccess()
    {
        var health = new MirrorHealthTracker();
        var inner = new FakeHandler(_ => Status(HttpStatusCode.NotFound));
        using var client = new HttpClient(Build(inner, health, Enabled()));

        using var response = await client.GetAsync(AssetUrl);

        // Direct 404 reaches the caller (the asset genuinely does not exist)…
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        // …but the mirror was not credited with a success.
        Assert.Equal(1, health.ConsecutiveFailures("https://m1.example/"));
    }

    [Fact]
    public async Task MirrorSizeMismatch_IsRejectedAndFallsBack()
    {
        // Release asset says 100 bytes; the mirror answers with 0 → interstitial page, not a DLL.
        var guard = new StubIntegrityGuard(DigestExpectation.Sha256(new string('a', 64), expectedLength: 100));
        var inner = new FakeHandler(_ => Ok());
        using var client = new HttpClient(Build(inner, new MirrorHealthTracker(),
            Enabled("https://m1.example/", "https://m2.example/"), guard));

        using var response = await client.GetAsync(AssetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, inner.Urls.Count);          // both mirrors rejected, direct succeeded
        Assert.Equal(AssetUrl, inner.Urls[2]);
    }

    [Fact]
    public async Task MirrorWithExpectation_ContentStillReadableAndVerified()
    {
        const string payload = "hello";
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var guard = new StubIntegrityGuard(DigestExpectation.Sha256(sha, expectedLength: payload.Length));
        var inner = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/plain"),
        });
        using var client = new HttpClient(Build(inner, new MirrorHealthTracker(), Enabled(), guard));

        using var response = await client.GetAsync(AssetUrl);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(payload, body);
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    /// <summary>Answers "nothing to verify" so the pipeline stays offline in tests.</summary>
    private sealed class SilentIntegrityGuard : IDownloadIntegrityGuard
    {
        public Task<DigestExpectation?> GetExpectationAsync(
            Uri originalUri,
            long? contentLength,
            Func<Uri, CancellationToken, Task<string?>> fetchApiJson,
            CancellationToken cancellationToken) =>
            Task.FromResult<DigestExpectation?>(null);
    }

    /// <summary>Always returns the given expectation, so the size pre-filter can be exercised.</summary>
    private sealed class StubIntegrityGuard : IDownloadIntegrityGuard
    {
        private readonly DigestExpectation? _expectation;

        public StubIntegrityGuard(DigestExpectation? expectation) => _expectation = expectation;

        public Task<DigestExpectation?> GetExpectationAsync(
            Uri originalUri,
            long? contentLength,
            Func<Uri, CancellationToken, Task<string?>> fetchApiJson,
            CancellationToken cancellationToken) =>
            Task.FromResult(_expectation);
    }

    /// <summary>Records every URL the pipeline actually tried and answers from a callback.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = (req, _) => Task.FromResult(responder(req));

        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return _responder(request, cancellationToken);
        }
    }
}
