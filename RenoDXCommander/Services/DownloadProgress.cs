// DownloadProgress.cs — what the status bar shows while a file is downloading.
//
// Downloads used to be completely silent: a 44 MB DLSS package produced no UI feedback at all,
// which reads as "RHI is stuck" and makes users think the mirror is slow even when it is not.
// This is a static channel (same pattern as MirrorRuntime) because the download happens deep in
// services that must not know about the view-model.

using System.Diagnostics;

namespace RenoDXCommander.Services;

public static class DownloadProgress
{
    private static readonly object Gate = new();
    private static string? _current;

    /// <summary>Raised whenever the progress text changes (any thread).</summary>
    public static event Action? Changed;

    /// <summary>The line to show right now, or null when nothing is downloading.</summary>
    public static string? Current
    {
        get { lock (Gate) return _current; }
    }

    /// <summary>Publishes a progress line, e.g. "DLSS: 12.3/44.5 MB · 1.02 MB/s".</summary>
    public static void Report(string text)
    {
        lock (Gate) _current = text;
        Changed?.Invoke();
    }

    /// <summary>Clears the line (called when the download finishes or fails).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            if (_current is null) return;
            _current = null;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> while publishing
    /// "label: received/total MB · speed MB/s" about four times a second. Always clears on exit.
    /// </summary>
    /// <param name="onFraction">
    /// Optional 0..1 callback, so callers that already show a dialog progress bar can move it
    /// during the download instead of leaving it frozen at "10%".
    /// </param>
    public static async Task CopyWithProgressAsync(
        Stream source,
        Stream destination,
        long? totalBytes,
        string label,
        CancellationToken cancellationToken,
        Action<double>? onFraction = null)
    {
        var buffer = new byte[81920];
        long received = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;

                var elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    lastReport = elapsed;
                    Report(Format(label, received, totalBytes, elapsed));
                    if (onFraction is not null && totalBytes is > 0)
                        onFraction(Math.Clamp(received / (double)totalBytes.Value, 0, 1));
                }
            }

            // Final line so the last visible number matches what landed on disk.
            Report(Format(label, received, totalBytes, stopwatch.Elapsed));
            if (onFraction is not null && totalBytes is > 0) onFraction(1);
        }
        finally
        {
            Clear();
        }
    }

    private static string Format(string label, long received, long? totalBytes, TimeSpan elapsed)
    {
        var receivedMb = received / 1024.0 / 1024.0;
        var seconds = Math.Max(elapsed.TotalSeconds, 0.2);
        var speedMbps = receivedMb / seconds;

        if (totalBytes is > 0)
        {
            var totalMb = totalBytes.Value / 1024.0 / 1024.0;
            var percent = received * 100.0 / totalBytes.Value;
            return $"Downloading {label}: {receivedMb:0.0}/{totalMb:0.0} MB ({percent:0}%) · {speedMbps:0.00} MB/s";
        }

        return $"Downloading {label}: {receivedMb:0.0} MB · {speedMbps:0.00} MB/s";
    }
}
