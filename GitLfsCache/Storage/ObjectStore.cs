// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
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
/// <c>{root}/{upstream}/staging</c>, where <c>{upstream}</c> is the key's directory name (see
/// <see cref="DirectoryNameFor"/>). Staging shares the volume with the objects so publishing is an
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

	// The staging files currently open for writing. Windows would refuse to delete these anyway,
	// because they are opened with FileShare.Read, but POSIX allows unlinking an open file: the
	// writer keeps its descriptor and the completed transfer then has nothing left to publish. The
	// guarantee has to come from here to hold on both.
	private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _openStaging = new(StringComparer.Ordinal);
	private long _totalBytes;

	/// <inheritdoc />
	public long TotalBytes => Interlocked.Read(ref _totalBytes);

	/// <inheritdoc />
	public Stream? OpenRead(string upstream, string oid, out long length)
	{
		length = 0;

		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			return null;
		}

		AbsoluteFilePath path = ObjectPath(upstream, oid);

		try
		{
			if (!fileSystem.File.Exists(path))
			{
				return null;
			}

			length = fileSystem.FileInfo.New(path).Length;

			// FileShare.Delete lets an eviction sweep remove this file while it is being served,
			// which Windows otherwise refuses outright.
			return fileSystem.FileStream.New(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			StoreLog.CouldNotOpenObject(logger, failure, oid, upstream);
			length = 0;
			return null;
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

		_openStaging[path.ToString()] = 0;

		return new StagingHandle(fileSystem, path, sink, closed => _openStaging.TryRemove(closed.ToString(), out _));
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

		// A tee abandons a failed staging write and carries on serving the client, so a faulted handle
		// can reach here. Its file is short by at least the failed write, and when that write was the
		// last one the digest can still match, so the digest alone is not enough to publish on.
		if (handle.Faulted)
		{
			StoreLog.DiscardedIncompleteObject(logger, upstream, oid);
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}

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
			// The check above and the rename are not one atomic step, so another request publishing
			// the same object can land between them and the rename then fails on an occupied
			// destination. Uploads never go through the fetch coalescer, so two clients pushing one
			// blob reach here with nothing coordinating them. Content addressing makes the file that
			// arrived byte-identical to this one, which is the same outcome the check reports: the
			// winner stands. Reporting it as a failure would have the caller record a verification
			// failure, and a benign race is not something to alert on.
			if (fileSystem.File.Exists(destination))
			{
				await handle.DisposeAsync().ConfigureAwait(false);
				return true;
			}

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
			string upstream = UpstreamFor(fileSystem.Path.GetFileName(upstreamDirectory));
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

		// A transfer is still writing to this file. Reporting false leaves it for a later sweep,
		// which is what the caller already does for a file the host refused to delete.
		return !_openStaging.ContainsKey(staged.Path.ToString()) && TryDeleteFile(staged.Path);
	}

	/// <inheritdoc />
	public int RecomputeTotalBytes()
	{
		int count = 0;
		long bytes = 0;

		foreach (StoredObject stored in Enumerate())
		{
			count++;
			bytes += stored.Size;
		}

		Interlocked.Exchange(ref _totalBytes, bytes);
		return count;
	}

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
		.Combine(_root, DirectoryNameFor(upstream), ObjectsDirectoryName, oid[..2], oid[2..4], oid)
		.As<AbsoluteFilePath>();

	private AbsoluteDirectoryPath StagingDirectory(string upstream) => fileSystem.Path
		.Combine(_root, DirectoryNameFor(upstream), StagingDirectoryName)
		.As<AbsoluteDirectoryPath>();

	/// <summary>
	/// Rejects anything that is not exactly 64 lowercase hex characters.
	/// </summary>
	/// <remarks>
	/// Object ids reach this store from a token the proxy itself signed, so this is defense in depth
	/// rather than the only guard. It is still worth having: it is the difference between a bug in the
	/// token layer being a cache miss and being a path traversal.
	/// </remarks>
	private static bool IsValidOid([NotNullWhen(true)] string? oid) =>
		oid is not null && oid.Length == OidLength && oid.All(char.IsAsciiHexDigitLower);

	private static bool IsValidUpstream([NotNullWhen(true)] string? upstream) => !string.IsNullOrEmpty(upstream);

	/// <summary>
	/// Maps an upstream key to the name of the directory its tree lives in.
	/// </summary>
	/// <remarks>
	/// A key made only of ASCII letters, digits, <c>-</c> and <c>_</c> is its own directory name, which
	/// is the layout every store written before other keys were supported already has. Any other key,
	/// such as the natural <c>gitlab.com</c>, has each byte of its UTF-8 form outside that set written
	/// as <c>%XX</c>. That is deterministic and reversible, so no two keys share a directory, and an
	/// escaped name always contains a <c>%</c>, which a plain one never does. It also leaves nothing a
	/// filesystem would read as a separator or a parent reference, so no key can name a directory
	/// outside the store root.
	/// </remarks>
	/// <param name="upstream">The upstream key.</param>
	/// <returns>The directory name.</returns>
	internal static string DirectoryNameFor(string upstream)
	{
		if (upstream.All(IsPlainCharacter))
		{
			return upstream;
		}

		StringBuilder name = new(upstream.Length * 3);

		foreach (byte value in Encoding.UTF8.GetBytes(upstream))
		{
			if (value < 0x80 && IsPlainCharacter((char)value))
			{
				name.Append((char)value);
			}
			else
			{
				name.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
			}
		}

		return name.ToString();
	}

	/// <summary>
	/// Recovers the upstream key from a directory name <see cref="DirectoryNameFor"/> produced.
	/// </summary>
	/// <param name="directoryName">The directory name.</param>
	/// <returns>The upstream key.</returns>
	internal static string UpstreamFor(string directoryName) =>
		directoryName.Contains('%', StringComparison.Ordinal)
			? Uri.UnescapeDataString(directoryName)
			: directoryName;

	private static bool IsPlainCharacter(char character) =>
		char.IsAsciiLetterOrDigit(character) || character is '-' or '_';
}
