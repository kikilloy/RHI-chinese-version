using System.Security.Cryptography;
using System.Text;
using RenoDXCommander.Services.Net;
using Xunit;

namespace RenoDXCommander.Tests;

/// <summary>
/// The stream wrapper that verifies while the caller copies the file to disk. A mismatch must
/// surface as InvalidDataException (so the caller discards the file), never as a silent pass.
/// </summary>
public class IntegrityVerifyingStreamTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task MatchingContent_PassesThrough()
    {
        var payload = Bytes("hello mirror");
        using var inner = new MemoryStream(payload);
        using var verifying = new IntegrityVerifyingStream(inner, DigestExpectation.Sha256(Sha256Hex(payload)));

        using var sink = new MemoryStream();
        await verifying.CopyToAsync(sink);

        Assert.Equal(payload, sink.ToArray());
        Assert.True(verifying.Verified);
    }

    [Fact]
    public async Task MismatchedContent_Throws_AndReportsActualHash()
    {
        var payload = Bytes("tampered payload");
        var expected = Sha256Hex(Bytes("the real payload"));
        string? reported = null;

        using var inner = new MemoryStream(payload);
        using var verifying = new IntegrityVerifyingStream(
            inner, DigestExpectation.Sha256(expected), actual => reported = actual);

        await Assert.ThrowsAsync<InvalidDataException>(() => verifying.CopyToAsync(new MemoryStream()));
        Assert.Equal(Sha256Hex(payload), reported);
    }

    [Fact]
    public async Task GitBlobSha1_IsVerifiedWithLengthPrefix()
    {
        var payload = Bytes("test");
        using var inner = new MemoryStream(payload);
        using var verifying = new IntegrityVerifyingStream(
            inner,
            DigestExpectation.GitBlobSha1("30d74d258442c7c65512eafab474568dd706c430", payload.Length));

        using var sink = new MemoryStream();
        await verifying.CopyToAsync(sink);

        Assert.Equal(payload, sink.ToArray());
        Assert.True(verifying.Verified);
    }

    [Fact]
    public async Task AbandonedStream_DoesNotClaimVerification()
    {
        var payload = Bytes("a much longer payload than we will read");
        using var inner = new MemoryStream(payload);
        var verifying = new IntegrityVerifyingStream(inner, DigestExpectation.Sha256(new string('a', 64)));

        var buffer = new byte[4];
        var read = await verifying.ReadAsync(buffer);
        Assert.Equal(4, read);

        verifying.Dispose();
        Assert.False(verifying.Verified);   // no throw, but nothing was verified either
    }

    [Fact]
    public void StreamIsReadOnlyAndUnseekable()
    {
        using var verifying = new IntegrityVerifyingStream(
            new MemoryStream(Bytes("x")), DigestExpectation.Sha256(new string('0', 64)));

        Assert.True(verifying.CanRead);
        Assert.False(verifying.CanSeek);
        Assert.False(verifying.CanWrite);
        Assert.Throws<NotSupportedException>(() => verifying.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => _ = verifying.Length);
    }

    [Fact]
    public async Task VerifyingHttpContent_WrapsBufferedReads()
    {
        var payload = Bytes("content that flows through http content");
        using var inner = new ByteArrayContentWithHeaders(payload);
        inner.Headers.ContentLength = payload.Length;

        using var wrapped = new VerifyingHttpContent(inner, DigestExpectation.Sha256(Sha256Hex(payload)));
        var bytes = await wrapped.ReadAsByteArrayAsync();

        Assert.Equal(payload, bytes);
    }

    [Fact]
    public async Task VerifyingHttpContent_ThrowsOnMismatch()
    {
        var payload = Bytes("not what was promised");
        using var inner = new ByteArrayContentWithHeaders(payload);
        inner.Headers.ContentLength = payload.Length;

        using var wrapped = new VerifyingHttpContent(
            inner, DigestExpectation.Sha256(Sha256Hex(Bytes("something else"))));

        await Assert.ThrowsAsync<InvalidDataException>(() => wrapped.ReadAsByteArrayAsync());
    }

    /// <summary>ByteArrayContent, but with a Content-Length we can control.</summary>
    private sealed class ByteArrayContentWithHeaders : HttpContent
    {
        private readonly byte[] _payload;

        public ByteArrayContentWithHeaders(byte[] payload) => _payload = payload;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            stream.WriteAsync(_payload, 0, _payload.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(_payload, writable: false));
    }
}
