// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// A read-only stream that copies everything read out of it into a secondary sink.
/// </summary>
/// <remarks>
/// This is the upload counterpart to <see cref="StreamTee"/>. On the download path the proxy drives
/// the copy, so a write-side tee works. On the upload path <c>HttpClient</c> drives it, pulling from
/// the client's body as it sends upstream, so the only place to observe the bytes is on the way out of
/// the read.
/// <para>
/// The sink is fail-soft for the same reason as on the download path: an upload that reaches upstream
/// but fails to cache is a successful push with a cold cache, which is far better than a failed push.
/// </para>
/// </remarks>
/// <param name="source">The stream being read, typically the incoming request body.</param>
/// <param name="sink">The secondary sink to copy into.</param>
/// <param name="onSinkFailure">Invoked once if the sink fails.</param>
internal sealed class ReadTeeStream(Stream source, Stream sink, Action<Exception>? onSinkFailure) : Stream
{
	private bool _sinkFailed;

	/// <inheritdoc />
	public override bool CanRead => true;

	/// <inheritdoc />
	public override bool CanSeek => false;

	/// <inheritdoc />
	public override bool CanWrite => false;

	/// <inheritdoc />
	public override long Length => throw new NotSupportedException();

	/// <inheritdoc />
	public override long Position
	{
		get => BytesRead;
		set => throw new NotSupportedException();
	}

	/// <summary>Gets the number of bytes read so far.</summary>
	public long BytesRead { get; private set; }

	/// <summary>Gets a value indicating whether the secondary sink is still being written to.</summary>
	public bool SinkIsLive => !_sinkFailed;

	/// <inheritdoc />
	public override void Flush()
	{
	}

	/// <inheritdoc />
	public override int Read(byte[] buffer, int offset, int count)
	{
		int read = source.Read(buffer, offset, count);
		CopyToSink(buffer.AsSpan(offset, read));
		return read;
	}

	/// <inheritdoc />
	public override async ValueTask<int> ReadAsync(
		Memory<byte> buffer,
		CancellationToken cancellationToken = default)
	{
		int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

		if (read > 0 && !_sinkFailed)
		{
			try
			{
				await sink.WriteAsync(buffer[..read], cancellationToken).ConfigureAwait(false);
			}
			catch (Exception failure) when (failure is IOException or ObjectDisposedException
				or UnauthorizedAccessException or NotSupportedException)
			{
				_sinkFailed = true;
				onSinkFailure?.Invoke(failure);
			}
		}

		BytesRead += read;
		return read;
	}

	/// <inheritdoc />
	public override Task<int> ReadAsync(
		byte[] buffer,
		int offset,
		int count,
		CancellationToken cancellationToken) =>
		ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	/// <inheritdoc />
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void SetLength(long value) => throw new NotSupportedException();

	/// <inheritdoc />
	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	private void CopyToSink(ReadOnlySpan<byte> buffer)
	{
		if (buffer.Length == 0 || _sinkFailed)
		{
			BytesRead += buffer.Length;
			return;
		}

		try
		{
			sink.Write(buffer);
		}
		catch (Exception failure) when (failure is IOException or ObjectDisposedException
			or UnauthorizedAccessException or NotSupportedException)
		{
			_sinkFailed = true;
			onSinkFailure?.Invoke(failure);
		}

		BytesRead += buffer.Length;
	}
}
