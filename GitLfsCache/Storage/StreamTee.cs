// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.Buffers;

/// <summary>
/// Copies one source stream to two sinks, where the second sink is allowed to fail.
/// </summary>
/// <remarks>
/// This is how an object is cached on the way through: the client is the primary sink and the
/// staging file is the secondary. The asymmetry is deliberate. A client not receiving its bytes is a
/// failed request, while a store write that fails only means the cache stays cold, so the secondary
/// is abandoned and the transfer continues.
/// <para>
/// Nothing is buffered whole, so memory use is flat regardless of object size. A 20 GB object and a
/// 20 MB object have the same profile, which is what makes container memory limits easy to set.
/// </para>
/// </remarks>
public static class StreamTee
{
	private const int BufferSize = 81_920;

	/// <summary>
	/// Copies <paramref name="source"/> to both sinks.
	/// </summary>
	/// <param name="source">The stream to read.</param>
	/// <param name="primary">The sink whose failures propagate.</param>
	/// <param name="secondary">The sink whose failures are reported and then ignored, or null.</param>
	/// <param name="onSecondaryFailure">Invoked once if the secondary sink fails.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The number of bytes written to <paramref name="primary"/>.</returns>
	public static async Task<long> CopyAsync(
		Stream source,
		Stream primary,
		Stream? secondary,
		Action<Exception>? onSecondaryFailure,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(source);
		Ensure.NotNull(primary);

		byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
		Stream? liveSecondary = secondary;
		long total = 0;

		try
		{
			while (true)
			{
				int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
					.ConfigureAwait(false);

				if (read == 0)
				{
					break;
				}

				await primary.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				total += read;

				if (liveSecondary is not null)
				{
					try
					{
						await liveSecondary.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
							.ConfigureAwait(false);
					}
					catch (Exception failure) when (failure is IOException or ObjectDisposedException
						or UnauthorizedAccessException or NotSupportedException)
					{
						// Narrow on purpose. Swallowing everything would hide programming errors, and
						// a cancellation must never be mistaken for a store failure.
						liveSecondary = null;
						onSecondaryFailure?.Invoke(failure);
					}
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}

		return total;
	}
}
