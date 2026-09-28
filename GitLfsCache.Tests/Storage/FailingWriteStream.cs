// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Storage;

/// <summary>
/// A sink that forwards a fixed number of writes and then fails every write after them the way a
/// full disk does.
/// </summary>
/// <param name="inner">The sink to forward the allowed writes to.</param>
/// <param name="allowedWrites">How many writes succeed before the first failure.</param>
internal sealed class FailingWriteStream(Stream inner, int allowedWrites) : Stream
{
	private int _writes;

	public override bool CanRead => false;

	public override bool CanSeek => false;

	public override bool CanWrite => true;

	public override long Length => inner.Length;

	public override long Position
	{
		get => inner.Position;
		set => throw new NotSupportedException();
	}

	public override void Flush() => inner.Flush();

	public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count)
	{
		ThrowIfExhausted();
		inner.Write(buffer, offset, count);
	}

	public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ThrowIfExhausted();
		await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
	}

	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
		WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

	public override async ValueTask DisposeAsync()
	{
		await inner.DisposeAsync().ConfigureAwait(false);
		await base.DisposeAsync().ConfigureAwait(false);
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			inner.Dispose();
		}

		base.Dispose(disposing);
	}

	private void ThrowIfExhausted()
	{
		if (_writes++ >= allowedWrites)
		{
			throw new IOException("No space left on device");
		}
	}
}
