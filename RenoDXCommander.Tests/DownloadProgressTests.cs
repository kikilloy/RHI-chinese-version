using System.Text;
using RenoDXCommander.Services;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// Downloads used to be invisible; a 44 MB package produced no feedback and looked like a freeze.
/// The progress channel must always clean up after itself, or the status bar keeps claiming a
/// download that already finished.
/// </summary>
public class DownloadProgressTests
{
    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task Copy_CopiesEveryByte_AndClearsWhenDone()
    {
        var payload = Payload(200_000);
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();

        await DownloadProgress.CopyWithProgressAsync(
            source, destination, payload.Length, "Test", CancellationToken.None);

        Assert.Equal(payload, destination.ToArray());
        Assert.Null(DownloadProgress.Current);   // never leaves a stale line behind
    }

    [Fact]
    public async Task Copy_ReportsFractionUpToOne()
    {
        var payload = Payload(150_000);
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();
        var fractions = new List<double>();

        await DownloadProgress.CopyWithProgressAsync(
            source, destination, payload.Length, "Test", CancellationToken.None, fractions.Add);

        Assert.NotEmpty(fractions);
        Assert.All(fractions, f => Assert.InRange(f, 0, 1));
        Assert.Equal(1, fractions[^1]);
    }

    [Fact]
    public async Task Copy_WithoutTotalAndWithoutCallback_StillCopies()
    {
        var payload = Payload(10_000);
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();

        await DownloadProgress.CopyWithProgressAsync(
            source, destination, totalBytes: null, "Unknown size", CancellationToken.None);

        Assert.Equal(payload.Length, destination.Length);
        Assert.Null(DownloadProgress.Current);
    }

    [Fact]
    public async Task Copy_IsCancellable_AndStillClears()
    {
        using var source = new MemoryStream(Payload(100_000));
        using var destination = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DownloadProgress.CopyWithProgressAsync(source, destination, 100_000, "Test", cts.Token));

        Assert.Null(DownloadProgress.Current);
    }

    [Fact]
    public void ReportAndClear_RaiseChanged()
    {
        var changes = 0;
        void OnChanged() => changes++;

        DownloadProgress.Changed += OnChanged;
        try
        {
            DownloadProgress.Report("Downloading Test: 1.0/2.0 MB (50%) · 1.00 MB/s");
            Assert.Equal("Downloading Test: 1.0/2.0 MB (50%) · 1.00 MB/s", DownloadProgress.Current);

            DownloadProgress.Clear();
            Assert.Null(DownloadProgress.Current);

            Assert.Equal(2, changes);
        }
        finally
        {
            DownloadProgress.Changed -= OnChanged;
            DownloadProgress.Clear();
        }
    }
}
