// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Storage;

using ktsu.GitLfsCache.Storage;

/// <summary>
/// A store that behaves exactly like the one it wraps except that deleting a stored object always
/// fails.
/// </summary>
/// <remarks>
/// A sweep has to survive a delete it cannot control: a permission, another process, a scanner.
/// Reproducing that by holding the file open does not work, and not only because the mock file
/// system follows the host's rules about it. The store asks for FileShare.Delete precisely so that
/// a sweep <em>can</em> remove an object while it is being served, so an open handle is the one
/// thing that is deliberately not supposed to stop it.
/// </remarks>
/// <param name="inner">The store to delegate to.</param>
/// <param name="undeletableOid">The object id whose deletion always fails.</param>
internal sealed class UndeletableObjectStore(IObjectStore inner, string undeletableOid) : IObjectStore
{
	public long TotalBytes => inner.TotalBytes;

	public Stream? OpenRead(string upstream, string oid, out long length) => inner.OpenRead(upstream, oid, out length);

	public StagingHandle OpenStaging(string upstream) => inner.OpenStaging(upstream);

	public Task<bool> PublishAsync(StagingHandle handle, string upstream, string oid, CancellationToken cancellationToken) =>
		inner.PublishAsync(handle, upstream, oid, cancellationToken);

	public void Touch(string upstream, string oid) => inner.Touch(upstream, oid);

	public bool Exists(string upstream, string oid) => inner.Exists(upstream, oid);

	public IEnumerable<StoredObject> Enumerate() => inner.Enumerate();

	public IEnumerable<StagedFile> EnumerateStaging() => inner.EnumerateStaging();

	/// <summary>Reports failure for the one object under test and delegates the rest.</summary>
	/// <param name="storedObject">The object to delete.</param>
	/// <returns>False for the undeletable object, otherwise whatever the inner store reports.</returns>
	public bool TryDelete(StoredObject storedObject) =>
		storedObject is not null
		&& !string.Equals(storedObject.Oid, undeletableOid, StringComparison.Ordinal)
		&& inner.TryDelete(storedObject);

	public bool TryDeleteStaging(StagedFile staged) => inner.TryDeleteStaging(staged);

	public int RecomputeTotalBytes() => inner.RecomputeTotalBytes();
}
