// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Storage;

using ktsu.GitLfsCache.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class StreamTeeTests
{
	private static byte[] Payload(int length)
	{
		byte[] payload = new byte[length];

		for (int index = 0; index < length; index++)
		{
			payload[index] = (byte)(index % 251);
		}

		return payload;
	}

	[TestMethod]
	public async Task CopyAsync_WritesTheSameBytesToBothSinks()
	{
		byte[] payload = Payload(300_000);
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();
		using MemoryStream secondary = new();

		long copied = await StreamTee.CopyAsync(source, primary, secondary, null, CancellationToken.None);

		Assert.AreEqual(payload.Length, copied);
		CollectionAssert.AreEqual(payload, primary.ToArray());
		CollectionAssert.AreEqual(payload, secondary.ToArray());
	}

	[TestMethod]
	public async Task CopyAsync_NoSecondary_StillCopiesToPrimary()
	{
		byte[] payload = Payload(1024);
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();

		long copied = await StreamTee.CopyAsync(source, primary, null, null, CancellationToken.None);

		Assert.AreEqual(payload.Length, copied);
		CollectionAssert.AreEqual(payload, primary.ToArray());
	}

	[TestMethod]
	public async Task CopyAsync_EmptySource_CopiesNothingAndSucceeds()
	{
		using MemoryStream source = new([]);
		using MemoryStream primary = new();
		using MemoryStream secondary = new();

		long copied = await StreamTee.CopyAsync(source, primary, secondary, null, CancellationToken.None);

		Assert.AreEqual(0L, copied);
		Assert.AreEqual(0, primary.Length);
	}

	[TestMethod]
	public async Task CopyAsync_SecondaryThrows_PrimaryStillReceivesEverything()
	{
		byte[] payload = Payload(300_000);
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();
		using ThrowingStream secondary = new(failAfterBytes: 1024);
		List<Exception> failures = [];

		long copied = await StreamTee.CopyAsync(source, primary, secondary, failures.Add, CancellationToken.None);

		Assert.AreEqual(payload.Length, copied);
		CollectionAssert.AreEqual(payload, primary.ToArray());
		Assert.HasCount(1, failures);
		Assert.IsInstanceOfType<IOException>(failures[0]);
	}

	[TestMethod]
	public async Task CopyAsync_PrimaryThrows_Propagates()
	{
		using MemoryStream source = new(Payload(300_000));
		using ThrowingStream primary = new(failAfterBytes: 1024);
		using MemoryStream secondary = new();

		await Assert.ThrowsExactlyAsync<IOException>(async () =>
			await StreamTee.CopyAsync(source, primary, secondary, null, CancellationToken.None));
	}

	[TestMethod]
	public async Task CopyAsync_Cancelled_ThrowsWithoutReportingAStoreFailure()
	{
		using MemoryStream source = new(Payload(300_000));
		using MemoryStream primary = new();
		using MemoryStream secondary = new();
		List<Exception> failures = [];
		using CancellationTokenSource cancellation = new();
		await cancellation.CancelAsync();

		await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
			await StreamTee.CopyAsync(source, primary, secondary, failures.Add, cancellation.Token));

		Assert.HasCount(0, failures);
	}

	[TestMethod]
	public async Task CopyAsync_ThroughAHashingStream_DigestsWhatWasWritten()
	{
		byte[] payload = Payload(300_000);
		string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();
		using MemoryStream sink = new();
		HashingStream hashing = new(sink, ownsInner: false);

		await using (hashing)
		{
			await StreamTee.CopyAsync(source, primary, hashing, null, CancellationToken.None);
			Assert.AreEqual(expected, hashing.GetDigestHex());
		}

		CollectionAssert.AreEqual(payload, sink.ToArray());
	}

	[TestMethod]
	public void HashingStream_GetDigestHexTwice_ReturnsTheSameValue()
	{
		using MemoryStream sink = new();
		using HashingStream hashing = new(sink, ownsInner: false);
		hashing.Write([1, 2, 3]);

		Assert.AreEqual(hashing.GetDigestHex(), hashing.GetDigestHex());
	}

	[TestMethod]
	public void HashingStream_NothingWritten_DigestsTheEmptyInput()
	{
		using MemoryStream sink = new();
		using HashingStream hashing = new(sink, ownsInner: false);

		Assert.AreEqual(
			Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([])),
			hashing.GetDigestHex());
	}

	/// <summary>Test double that accepts a fixed number of bytes and then fails every write.</summary>
	private sealed class ThrowingStream(int failAfterBytes) : Stream
	{
		private long _written;

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => _written;

		public override long Position
		{
			get => _written;
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			if (_written >= failAfterBytes)
			{
				throw new IOException("Simulated sink failure.");
			}

			_written += buffer.Length;
			return ValueTask.CompletedTask;
		}
	}
}
