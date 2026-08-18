// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using ktsu.GitLfsCache.Configuration;
using ktsu.Semantics.Paths;
using ktsu.Semantics.Strings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Content-addressed object store over an abstracted filesystem.
/// </summary>
/// <remarks>
/// Layout is <c>{root}/{upstream}/objects/{first two}/{next two}/{oid}</c> with staging under
/// <c>{root}/{upstream}/staging</c>. Staging shares the volume with the objects so publishing is an
/// atomic rename, and the two-level fan-out mirrors the git-lfs client's own layout, which keeps
/// directory sizes reasonable into the hundreds of thousands of objects.
/// <para>
/// Access times are set explicitly rather than read from the filesystem, because <c>noatime</c> and
/// <c>relatime</c> mounts make filesystem access times unreliable and eviction depends on them.
/// </para>
/// </remarks>
/// <param name="fileSystem">The filesystem to store objects on.</param>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so access times are testable.</param>
/// <param name="logger">Logger.</param>
public sealed class ObjectStore(
	IFileSystem fileSystem,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider,
	ILogger<ObjectStore> logger) : IObjectStore
{
	private const string ObjectsDirectoryName = "objects";
	private const string StagingDirectoryName = "staging";
	private const int OidLength = 64;

	private readonly AbsoluteDirectoryPath _root = options.Value.Store.Root.As<AbsoluteDirectoryPath>();
	private long _totalBytes;

	/// <inheritdoc />
	public long TotalBytes => Interlocked.Read(ref _totalBytes);

	/// <inheritdoc />
	public bool TryOpenRead(string upstream, string oid, out Stream? stream, out long length)
	{
		stream = null;
		length = 0;

		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			return false;
		}

		AbsoluteFilePath path = ObjectPath(upstream, oid);

		try
		{
			if (!fileSystem.File.Exists(path))
			{
				return false;
			}

			length = fileSystem.FileInfo.New(path).Length;

			// FileShare.Delete lets an eviction sweep remove this file while it is being served,
			// which Windows otherwise refuses outright.
			stream = fileSystem.FileStream.New(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);

			return true;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			StoreLog.CouldNotOpenObject(logger, failure, oid, upstream);
			stream?.Dispose();
			stream = null;
			length = 0;
			return false;
		}
	}

	/// <inheritdoc />
	public StagingHandle OpenStaging(string upstream)
	{
		if (!IsValidUpstream(upstream))
		{
			throw new ArgumentException($"'{upstream}' is not a valid upstream key.", nameof(upstream));
		}

		AbsoluteDirectoryPath directory = StagingDirectory(upstream);
		fileSystem.Directory.CreateDirectory(directory);

		AbsoluteFilePath path = fileSystem.Path
			.Combine(directory, $"{Guid.NewGuid():N}.tmp")
			.As<AbsoluteFilePath>();

		Stream sink = fileSystem.FileStream.New(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

		return new StagingHandle(fileSystem, path, sink);
	}

	/// <inheritdoc />
	public async Task<bool> PublishAsync(
		StagingHandle handle,
		string upstream,
		string oid,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(handle);

		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}

		// Read the digest before closing, then release the write handle so the rename is not blocked.
		string digest = handle.GetDigestHex();
		await handle.CloseAsync(cancellationToken).ConfigureAwait(false);

		if (!string.Equals(digest, oid, StringComparison.OrdinalIgnoreCase))
		{
			StoreLog.DiscardedMismatchedObject(logger, upstream, digest, oid);
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}

		AbsoluteFilePath destination = ObjectPath(upstream, oid);

		try
		{
			fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(destination)!);

			if (fileSystem.File.Exists(destination))
			{
				// Another request published the same content first. Content addressing makes the two
				// byte-identical, so the winner stands and this copy is dropped.
				await handle.DisposeAsync().ConfigureAwait(false);
				return true;
			}

			long size = fileSystem.FileInfo.New(handle.Path).Length;
			fileSystem.File.Move(handle.Path, destination);
			handle.MarkPublished();
			Touch(upstream, oid);
			Interlocked.Add(ref _totalBytes, size);
			return true;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			StoreLog.CouldNotPublishObject(logger, failure, oid, upstream);
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}
	}

	/// <inheritdoc />
	public void Touch(string upstream, string oid)
	{
		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			return;
		}

		try
		{
			AbsoluteFilePath path = ObjectPath(upstream, oid);

			if (fileSystem.File.Exists(path))
			{
				fileSystem.File.SetLastAccessTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
			}
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// A missed access time only makes this object look colder than it is, which is not worth
			// failing an otherwise successful request over.
			StoreLog.CouldNotUpdateAccessTime(logger, failure, oid);
		}
	}

	/// <inheritdoc />
	public bool Exists(string upstream, string oid) =>
		IsValidUpstream(upstream) && IsValidOid(oid) && fileSystem.File.Exists(ObjectPath(upstream, oid));

	/// <inheritdoc />
	public IEnumerable<StoredObject> Enumerate()
	{
		if (!fileSystem.Directory.Exists(_root))
		{
			yield break;
		}

		foreach (string upstreamDirectory in fileSystem.Directory.EnumerateDirectories(_root))
		{
			string upstream = fileSystem.Path.GetFileName(upstreamDirectory);
			string objectsDirectory = fileSystem.Path.Combine(upstreamDirectory, ObjectsDirectoryName);

			if (!fileSystem.Directory.Exists(objectsDirectory))
			{
				continue;
			}

			foreach (string file in fileSystem.Directory.EnumerateFiles(
				objectsDirectory, "*", SearchOption.AllDirectories))
			{
				string oid = fileSystem.Path.GetFileName(file);

				// A stray file dropped into the tree by hand is not a cached object, so it is neither
				// counted nor evicted as one.
				if (!IsValidOid(oid))
				{
					continue;
				}

				IFileInfo info = fileSystem.FileInfo.New(file);

				yield return new StoredObject(
					file.As<AbsoluteFilePath>(),
					upstream,
					oid,
					info.Length,
					new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero));
			}
		}
	}

	/// <inheritdoc />
	public IEnumerable<StagedFile> EnumerateStaging()
	{
		if (!fileSystem.Directory.Exists(_root))
		{
			yield break;
		}

		foreach (string upstreamDirectory in fileSystem.Directory.EnumerateDirectories(_root))
		{
			string stagingDirectory = fileSystem.Path.Combine(upstreamDirectory, StagingDirectoryName);

			if (!fileSystem.Directory.Exists(stagingDirectory))
			{
				continue;
			}

			foreach (string file in fileSystem.Directory.EnumerateFiles(stagingDirectory, "*.tmp"))
			{
				IFileInfo info = fileSystem.FileInfo.New(file);

				yield return new StagedFile(
					file.As<AbsoluteFilePath>(),
					new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero));
			}
		}
	}

	/// <inheritdoc />
	public bool TryDelete(StoredObject storedObject)
	{
		Ensure.NotNull(storedObject);

		if (TryDeleteFile(storedObject.Path))
		{
			Interlocked.Add(ref _totalBytes, -storedObject.Size);
			return true;
		}

		return false;
	}

	/// <inheritdoc />
	public bool TryDeleteStaging(StagedFile staged)
	{
		Ensure.NotNull(staged);
		return TryDeleteFile(staged.Path);
	}

	/// <inheritdoc />
	public void RecomputeTotalBytes() =>
		Interlocked.Exchange(ref _totalBytes, Enumerate().Sum(stored => stored.Size));

	private bool TryDeleteFile(AbsoluteFilePath path)
	{
		try
		{
			if (fileSystem.File.Exists(path))
			{
				fileSystem.File.Delete(path);
			}

			return true;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// On Windows a file another request has open cannot be deleted. Skipping it and retrying
			// on the next sweep is correct; the alternative is failing a live transfer.
			StoreLog.CouldNotDeleteFile(logger, failure, path);
			return false;
		}
	}

	private AbsoluteFilePath ObjectPath(string upstream, string oid) => fileSystem.Path
		.Combine(_root, upstream, ObjectsDirectoryName, oid[..2], oid[2..4], oid)
		.As<AbsoluteFilePath>();

	private AbsoluteDirectoryPath StagingDirectory(string upstream) => fileSystem.Path
		.Combine(_root, upstream, StagingDirectoryName)
		.As<AbsoluteDirectoryPath>();

	/// <summary>
	/// Rejects anything that is not exactly 64 lowercase hex characters.
	/// </summary>
	/// <remarks>
	/// Object ids reach this store from a token the proxy itself signed, so this is defense in depth
	/// rather than the only guard. It is still worth having: it is the difference between a bug in the
	/// token layer being a cache miss and being a path traversal.
	/// </remarks>
	private static bool IsValidOid([NotNullWhen(true)] string? oid)
	{
		if (oid is null || oid.Length != OidLength)
		{
			return false;
		}

		foreach (char character in oid)
		{
			if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsValidUpstream([NotNullWhen(true)] string? upstream)
	{
		if (string.IsNullOrEmpty(upstream))
		{
			return false;
		}

		foreach (char character in upstream)
		{
			if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-' or '_'))
			{
				return false;
			}
		}

		return true;
	}
}
