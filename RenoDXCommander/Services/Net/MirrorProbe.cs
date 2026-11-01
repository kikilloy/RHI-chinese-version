// MirrorProbe.cs — "Test mirrors" button backend.
//
// Deliberately bypasses the shared HttpClient: that pipeline rewrites URLs through whichever
// mirror is active, which would measure the active mirror instead of each candidate. Each probe
// gets its own connection so the numbers are not influenced by pooled sockets either.

using System.Diagnostics;
using System.Net.Http;

namespace RenoDXCommander.Services.Net;

/// <summary>One mirror's timing result.</summary>
public sealed record MirrorProbeResult(string Prefix, bool Ok, long ElapsedMs, string? Error)
{
    public string Host => MirrorPolicy.PrefixHost(Prefix);
}

public static class MirrorProbe
{
    /// <summary>Small file in the RHI repo — cheap for the mirror and for us.</summary>
    public const string ProbeUrl = "https://raw.githubusercontent.com/RankFTW/RHI/main/motd.md";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Probes every prefix in parallel and returns one result per prefix (never throws).</summary>
    public static async Task<IReadOnlyList<MirrorProbeResult>> TestAsync(
        IEnumerable<string> prefixes,
        CancellationToken cancellationToken = default)
    {
        var list = prefixes?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();
        if (list.Count == 0) return Array.Empty<MirrorProbeResult>();

        var tasks = list.Select(p => TestOneAsync(p, cancellationToken)).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task<MirrorProbeResult> TestOneAsync(string prefix, CancellationToken cancellationToken)
    {
        if (!MirrorPolicy.TryNormalizePrefix(prefix, out var normalized))
            return new MirrorProbeResult(prefix, false, -1, "not a usable https prefix");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) };
            using var client = new HttpClient(handler) { Timeout = ProbeTimeout };
            client.DefaultRequestHeaders.Add("User-Agent", "RHI/2.0");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);

            using var response = await client
                .GetAsync(normalized + ProbeUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);

            await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            return new MirrorProbeResult(
                normalized,
                response.IsSuccessStatusCode,
                stopwatch.ElapsedMilliseconds,
                response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new MirrorProbeResult(normalized, false, stopwatch.ElapsedMilliseconds, "timeout");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new MirrorProbeResult(normalized, false, stopwatch.ElapsedMilliseconds, ex.Message);
        }
    }
}
