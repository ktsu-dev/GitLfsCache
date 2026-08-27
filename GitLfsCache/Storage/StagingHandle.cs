// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.IO.Abstractions;
using ktsu.Semantics.Paths;

/// <summary>
/// A staging file being written before it is verified and published.
/// </summary>
/// <remarks>
/// Disposing without a successful publish deletes the file. That is what keeps a cancelled or failed
/// transfer from leaving a partial object behind, and it means no caller has to remember to clean up
/// on every failure path.
/// <para>
/// The exposed stream digests as it writes, so publishing can verify the content against the object
/// id without reading the file back.
/// </para>
/// </remarks>
public sealed class StagingHandle : IAsyncDisposable
{
	private readonly IFileSystem _fileSystem;
	private readonly HashingStream _stream;
	private readonly Action<AbsoluteFilePath>? _onClosed;
	private bool _published;
	private bool _disposed;

	internal StagingHandle(IFileSystem fileSystem, AbsoluteFilePath path, Stream sink, Action<AbsoluteFilePath>? onClosed = null)
	{
		_fileSystem = fileSystem;
		_stream = new HashingStream(sink);
		_onClosed = onClosed;
		Path = path;
	}

	/// <summary>Gets the absolute path of the staging file.</summary>
	public AbsoluteFilePath Path { get; }

	/// <summary>Gets the writable stream for the staging file.</summary>
	public Stream Stream => _stream;

	/// <summary>
	/// Gets the digest of everything written so far, as lowercase hex.
	/// </summary>
	/// <returns>The digest, in the form a Git LFS object id takes.</returns>
	public string GetDigestHex() => _stream.GetDigestHex();

	/// <summary>Marks the staging file as published so disposal leaves it alone.</summary>
	internal void MarkPublished() => _published = true;

	/// <summary>Flushes and closes the underlying file so it can be renamed.</summary>
	internal async ValueTask CloseAsync(CancellationToken cancellationToken)
	{
		await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		await _stream.DisposeAsync().ConfigureAwait(false);

		// Nothing is writing to the file once its stream is closed, so the guard lifts here rather
		// than at disposal. A crashed write that closed without publishing leaves an orphan that
		// cleanup is then free to collect.
		_onClosed?.Invoke(Path);
	}

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await _stream.DisposeAsync().ConfigureAwait(false);

		// Released whether or not the file was published, because either way nothing is still
		// writing to this path once the handle is gone.
		_onClosed?.Invoke(Path);

		if (_published)
		{
			return;
		}

		try
		{
			if (_fileSystem.File.Exists(Path))
			{
				_fileSystem.File.Delete(Path);
			}
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// Age-based staging cleanup will collect it. Throwing from disposal would turn a harmless
			// leftover file into a failed request.
		}
	}
}
