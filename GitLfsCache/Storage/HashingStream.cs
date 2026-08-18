// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.Security.Cryptography;

/// <summary>
/// A write-only stream that digests everything written to it on its way to an inner sink.
/// </summary>
/// <remarks>
/// This is why an object is verified without a second read. The spec originally called for hashing
/// the finished staging file through Essentials' <c>IHashProvider</c>, but that interface offers only
/// a synchronous <c>TryHash(Stream, ...)</c> and no incremental or async-stream form, so verifying a
/// multi-gigabyte object that way would mean reading the whole file a second time and blocking a
/// thread while doing it. Digesting during the write costs one pass and no blocking.
/// <para>
/// SHA256 is hard-coded rather than injected because Git LFS object ids are defined as SHA256
/// digests. There is no alternative to select.
/// </para>
/// </remarks>
/// <param name="inner">The sink to forward writes to.</param>
/// <param name="ownsInner">Whether disposing this stream should dispose the sink.</param>
internal sealed class HashingStream(Stream inner, bool ownsInner = true) : Stream
{
	private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
	private byte[]? _digest;
	private long _written;

	/// <inheritdoc />
	public override bool CanRead => false;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override bool CanWrite => true;

	/// <inheritdoc />
	public override long Length => _written;

	/// <inheritdoc />
	public override long Position
	{
		get => _written;
		set => throw new NotSupportedException();
	}

	/// <summary>
	/// Finishes the digest and returns it as lowercase hex, the form a Git LFS object id takes.
	/// </summary>
	/// <returns>The digest of everything written, as lowercase hex.</returns>
	public string GetDigestHex()
	{
		// IncrementalHash resets on GetHashAndReset, so the result is cached for repeat calls.
		_digest ??= _hash.GetHashAndReset();
		return Convert.ToHexStringLower(_digest);
	}

	/// <inheritdoc />
	public override void Flush() => inner.Flush();

	/// <inheritdoc />
	public override Task FlushAsync(CancellationToken cancellationToken) =>
		inner.FlushAsync(cancellationToken);

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void SetLength(long value) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count) =>
		Write(buffer.AsSpan(offset, count));

	/// <inheritdoc />
	public override void Write(ReadOnlySpan<byte> buffer)
	{
		_hash.AppendData(buffer);
		inner.Write(buffer);
		_written += buffer.Length;
	}

	/// <inheritdoc />
	public override async ValueTask WriteAsync(
		ReadOnlyMemory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		_hash.AppendData(buffer.Span);
		await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
		_written += buffer.Length;
	}

	/// <inheritdoc />
	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	/// <inheritdoc />
	public override async ValueTask DisposeAsync()
	{
		if (ownsInner)
		{
			await inner.DisposeAsync().ConfigureAwait(false);
		}

		_hash.Dispose();
		await base.DisposeAsync().ConfigureAwait(false);
	}

	/// <inheritdoc />
	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			if (ownsInner)
			{
				inner.Dispose();
			}

			_hash.Dispose();
		}

		base.Dispose(disposing);
	}
}
