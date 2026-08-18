// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// A content-addressed store of Git LFS objects, namespaced per upstream.
/// </summary>
public interface IObjectStore
{
	/// <summary>Gets the total size of every published object, in bytes.</summary>
	public long TotalBytes { get; }

	/// <summary>
	/// Opens a published object for reading.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	/// <param name="stream">The readable stream, or null when the object is absent.</param>
	/// <param name="length">The object length in bytes, or zero when absent.</param>
	/// <returns><see langword="true"/> when the object was opened.</returns>
	public bool TryOpenRead(string upstream, string oid, out Stream? stream, out long length);

	/// <summary>
	/// Creates a staging file to write an object into.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <returns>A handle that deletes the file unless it is published.</returns>
	/// <exception cref="ArgumentException"><paramref name="upstream"/> is not a valid key.</exception>
	public StagingHandle OpenStaging(string upstream);

	/// <summary>
	/// Verifies a staging file against an object id and publishes it on a match.
	/// </summary>
	/// <param name="handle">The staging handle, consumed by this call.</param>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The expected object id.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns><see langword="true"/> when the content hashed to <paramref name="oid"/>.</returns>
	public Task<bool> PublishAsync(
		StagingHandle handle,
		string upstream,
		string oid,
		CancellationToken cancellationToken);

	/// <summary>Records that an object was served, so eviction sees it as warm.</summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	public void Touch(string upstream, string oid);

	/// <summary>Reports whether an object is published.</summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	/// <returns><see langword="true"/> when the object is present.</returns>
	public bool Exists(string upstream, string oid);

	/// <summary>Enumerates every published object across every upstream.</summary>
	/// <returns>The published objects.</returns>
	public IEnumerable<StoredObject> Enumerate();

	/// <summary>Enumerates staging files across every upstream.</summary>
	/// <returns>The staging files.</returns>
	public IEnumerable<StagedFile> EnumerateStaging();

	/// <summary>Deletes a published object, reporting rather than throwing on failure.</summary>
	/// <param name="storedObject">The object to delete.</param>
	/// <returns><see langword="true"/> when the object was deleted.</returns>
	public bool TryDelete(StoredObject storedObject);

	/// <summary>Deletes a staging file, reporting rather than throwing on failure.</summary>
	/// <param name="staged">The staging file to delete.</param>
	/// <returns><see langword="true"/> when the file was deleted.</returns>
	public bool TryDeleteStaging(StagedFile staged);

	/// <summary>Rebuilds the byte counter by scanning the store.</summary>
	public void RecomputeTotalBytes();
}
