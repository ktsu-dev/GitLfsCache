// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// In-memory admission set keyed by a salted hash of the caller's credential.
/// </summary>
/// <remarks>
/// The credential itself is never stored. Entries are keyed by HMAC-SHA256 over the upstream, the
/// repository and the Authorization header, under a key generated per process and never written
/// anywhere, so a memory dump yields nothing replayable and two processes cannot correlate their
/// entries against each other.
/// <para>
/// Comparison of the header is ordinal and exact. Two spellings of the same credential simply admit
/// separately, which costs one extra upstream probe and never admits anything that was not proven.
/// </para>
/// </remarks>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so expiry is testable.</param>
public sealed class CredentialAdmission(
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider) : ICredentialAdmission, IDisposable
{
	/// <summary>
	/// How many expired entries may accumulate before a sweep.
	/// </summary>
	/// <remarks>
	/// Swept opportunistically on write rather than on a timer. The set is small by nature, one entry
	/// per active credential per repository, and a background timer for something that size would be
	/// more moving parts than the problem deserves.
	/// </remarks>
	private const int SweepThreshold = 1024;

	private readonly ConcurrentDictionary<string, DateTimeOffset> _admitted = new(StringComparer.Ordinal);
	private readonly HMACSHA256 _hash = new(RandomNumberGenerator.GetBytes(32));
	private readonly Lock _hashGate = new();

	/// <inheritdoc />
	public bool IsAdmitted(string upstream, string repositoryPath, string? authorization)
	{
		// An anonymous caller is never admitted. Upstream would refuse it, and admitting it here would
		// mean a listing served to someone who never proved anything.
		if (string.IsNullOrEmpty(authorization))
		{
			return false;
		}

		string key = Key(upstream, repositoryPath, authorization);

		if (!_admitted.TryGetValue(key, out DateTimeOffset expiry))
		{
			return false;
		}

		if (timeProvider.GetUtcNow() >= expiry)
		{
			// Removed on the way past rather than left to a sweep, so an expired entry cannot be read
			// twice by concurrent callers racing the same instant.
			_admitted.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, expiry));
			return false;
		}

		return true;
	}

	/// <inheritdoc />
	public void Admit(string upstream, string repositoryPath, string? authorization)
	{
		if (string.IsNullOrEmpty(authorization))
		{
			return;
		}

		_admitted[Key(upstream, repositoryPath, authorization)] =
			timeProvider.GetUtcNow() + options.Value.Locks.AdmissionTtl;

		if (_admitted.Count > SweepThreshold)
		{
			Sweep();
		}
	}

	/// <inheritdoc />
	public void Dispose() => _hash.Dispose();

	private void Sweep()
	{
		DateTimeOffset now = timeProvider.GetUtcNow();

		foreach ((string key, DateTimeOffset expiry) in _admitted)
		{
			if (now >= expiry)
			{
				_admitted.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, expiry));
			}
		}
	}

	/// <summary>
	/// Derives the entry key for one credential and repository.
	/// </summary>
	/// <remarks>
	/// The three parts are separated by a character that cannot appear in an upstream key, so no two
	/// different triples can produce the same input. Without that, an upstream and repository could be
	/// re-split to match a different pair.
	/// </remarks>
	private string Key(string upstream, string repositoryPath, string authorization)
	{
		byte[] input = Encoding.UTF8.GetBytes($"{upstream}\n{repositoryPath}\n{authorization}");

		// HMACSHA256 holds mutable state across ComputeHash, so one shared instance needs a gate. The
		// alternative, an instance per call, allocates on a path taken on every lock request.
		lock (_hashGate)
		{
			return Convert.ToHexStringLower(_hash.ComputeHash(input));
		}
	}
}
