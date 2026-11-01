// IntegrityVerifyingStream.cs — hashes bytes as they stream past and throws if they don't match.
//
// Why a stream and not "download, then hash": mirrored release assets can be hundreds of MB and
// the caller already streams them to disk with a progress bar. Wrapping the stream keeps memory
// flat and progress smooth, and a mismatch surfaces as InvalidDataException from the caller's
// own CopyToAsync — which is exactly the "discard this file" signal we want.

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace RenoDXCommander.Services.Net;

/// <summary>
/// Read-only pass-through stream that verifies the expected hash once the inner stream ends.
/// Unverified content (caller stopped reading early) is reported, never silently accepted.
/// </summary>
public sealed class IntegrityVerifyingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;
    private readonly DigestExpectation _expectation;
    private readonly Action<string>? _onMismatch;
    private long _bytesRead;
    private bool _completed;
    private bool _disposed;

    public IntegrityVerifyingStream(
        Stream inner,
        DigestExpectation expectation,
        Action<string>? onMismatch = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _expectation = expectation;
        _onMismatch = onMismatch;

        _hash = IncrementalHash.CreateHash(expectation.Algorithm == IntegrityAlgorithm.Sha256
            ? HashAlgorithmName.SHA256
            : HashAlgorithmName.SHA1);

        // Git blob ids hash "blob {length}\0" before the content.
        if (expectation.Algorithm == IntegrityAlgorithm.GitBlobSha1 && expectation.BlobLength >= 0)
            _hash.AppendData(GitHubDigest.BlobHeader(expectation.BlobLength));
    }

    /// <summary>True once the stream reached its end (a mismatch throws instead).</summary>
    public bool Verified => _completed;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0) Append(buffer.AsSpan(offset, read));
        else Complete();
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0) Append(buffer.Span[..read]);
        else Complete();
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Append(ReadOnlySpan<byte> data)
    {
        _bytesRead += data.Length;
        _hash.AppendData(data);
    }

    private void Complete()
    {
        if (_completed) return;
        _completed = true;

        var actual = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();

        if (!string.Equals(actual, _expectation.ExpectedHex, StringComparison.OrdinalIgnoreCase))
        {
            _onMismatch?.Invoke(actual);
            throw new InvalidDataException(
                $"[Integrity] {_expectation.Algorithm} mismatch: expected {_expectation.ExpectedHex}, got {actual} ({_bytesRead} bytes)");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_completed)
            {
                CrashReporter.Log($"[IntegrityVerifyingStream.Dispose] Verification skipped — stream abandoned after {_bytesRead} bytes (expected {_expectation.Algorithm} {_expectation.ExpectedHex})");
            }
            _hash.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Replaces <see cref="HttpResponseMessage.Content"/> for a mirrored response so that any way of
/// consuming it (stream copy, buffered read, ReadAsByteArrayAsync) passes through the verifier.
/// </summary>
public sealed class VerifyingHttpContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly DigestExpectation _expectation;
    private readonly Action<string>? _onMismatch;
    private readonly long _length;

    public VerifyingHttpContent(
        HttpContent inner,
        DigestExpectation expectation,
        Action<string>? onMismatch = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _expectation = expectation;
        _onMismatch = onMismatch;
        _length = inner.Headers.ContentLength ?? -1;

        foreach (var header in inner.Headers)
            Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        using var verified = await CreateVerifyingStreamAsync().ConfigureAwait(false);
        await verified.CopyToAsync(stream).ConfigureAwait(false);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        await CreateVerifyingStreamAsync().ConfigureAwait(false);

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return _length >= 0;
    }

    private async Task<IntegrityVerifyingStream> CreateVerifyingStreamAsync()
    {
        var innerStream = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        return new IntegrityVerifyingStream(innerStream, _expectation, _onMismatch);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
