// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// The store a metadata-only deployment has, which is none.
/// </summary>
/// <remarks>
/// Registered instead of <see cref="ObjectStore"/> when <c>Store:Enabled</c> is false. It exists to
/// satisfy the dependency rather than to do anything: with no store, the handler relays batch,
/// transfer and verify, so no caller reaches any member here.
/// <para>
/// A real store cannot simply be left unregistered, because <see cref="ObjectStore"/> resolves its
/// root to an absolute path in its constructor and a metadata-only deployment has no root to give it.
/// Nor is a store that silently discards writes the right answer: that would make this deployment a
/// bandwidth funnel for object bytes it has no disk for and was never placed to carry.
/// </para>
/// </remarks>
public sealed class NullObjectStore : IObjectStore
{
	/// <inheritdoc />
	public long TotalBytes => 0;

	/// <inheritdoc />
	public Stream? OpenRead(string upstream, string oid, out long length)
	{
		length = 0;
		return null;
	}

	/// <inheritdoc />
	/// <exception cref="NotSupportedException">Always, because nothing should reach this.</exception>
	public StagingHandle OpenStaging(string upstream) => throw new NotSupportedException(
		"This deployment has Store:Enabled set to false, so it relays object transfers rather than storing them. Reaching this means a transfer was routed to the store path, which is a bug in the handler's dispatch rather than a configuration problem.");

	/// <inheritdoc />
	public Task<bool> PublishAsync(
		StagingHandle handle,
		string upstream,
		string oid,
		CancellationToken cancellationToken) => Task.FromResult(false);

	/// <inheritdoc />
	public void Touch(string upstream, string oid)
	{
	}

	/// <inheritdoc />
	public bool Exists(string upstream, string oid) => false;

	/// <inheritdoc />
	public IEnumerable<StoredObject> Enumerate() => [];

	/// <inheritdoc />
	public IEnumerable<StagedFile> EnumerateStaging() => [];

	/// <inheritdoc />
	public bool TryDelete(StoredObject storedObject) => false;

	/// <inheritdoc />
	public bool TryDeleteStaging(StagedFile staged) => false;

	/// <inheritdoc />
	public int RecomputeTotalBytes() => 0;
}
