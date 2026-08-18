// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Splits a request path into the upstream key and the Git LFS endpoint it addresses.
/// </summary>
/// <remarks>
/// A repository path has variable depth (<c>owner/repo.git/info/lfs</c> against
/// <c>org/project/_git/repo/info/lfs</c>), and ASP.NET routing only allows a catch-all as the final
/// segment, so <c>{**repositoryPath}/objects/batch</c> is not a route it can express. One catch-all
/// route plus this parser handles it instead, which also makes the dispatch rules directly testable.
/// <para>
/// Anything not recognized becomes a relay rather than a failure. That is what lets a Git LFS feature
/// this proxy does not understand degrade to plain proxying.
/// </para>
/// </remarks>
public static class LfsRouteParser
{
	private const string ObjectsSegment = "objects";
	private const string BatchSegment = "batch";
	private const string VerifySegment = "verify";
	private const int OidLength = 64;

	/// <summary>
	/// Parses a request path.
	/// </summary>
	/// <param name="path">The request path, with or without a leading slash.</param>
	/// <param name="route">The parsed route, or null when there is no upstream segment at all.</param>
	/// <returns><see langword="true"/> when the path carries an upstream key.</returns>
	public static bool TryParse(string? path, [NotNullWhen(true)] out LfsRoute? route)
	{
		route = null;

		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (segments.Length == 0)
		{
			return false;
		}

		string upstream = segments[0];
		string[] rest = segments[1..];
		string relayPath = string.Join('/', rest);

		route = Classify(upstream, rest, relayPath);
		return true;
	}

	private static LfsRoute Classify(string upstream, string[] rest, string relayPath)
	{
		// .../objects/batch
		if (rest.Length >= 2
			&& rest[^1] == BatchSegment
			&& rest[^2] == ObjectsSegment)
		{
			return new LfsRoute(
				LfsRouteKind.Batch,
				upstream,
				string.Join('/', rest[..^2]),
				Oid: null,
				relayPath);
		}

		// .../objects/{oid}/verify
		if (rest.Length >= 3
			&& rest[^1] == VerifySegment
			&& rest[^3] == ObjectsSegment
			&& IsOid(rest[^2]))
		{
			return new LfsRoute(
				LfsRouteKind.Verify,
				upstream,
				string.Join('/', rest[..^3]),
				rest[^2],
				relayPath);
		}

		// .../objects/{oid}
		if (rest.Length >= 2
			&& rest[^2] == ObjectsSegment
			&& IsOid(rest[^1]))
		{
			return new LfsRoute(
				LfsRouteKind.Transfer,
				upstream,
				string.Join('/', rest[..^2]),
				rest[^1],
				relayPath);
		}

		return new LfsRoute(LfsRouteKind.Relay, upstream, string.Empty, Oid: null, relayPath);
	}

	/// <summary>
	/// Reports whether a segment is exactly 64 lowercase hex characters.
	/// </summary>
	/// <remarks>
	/// Uppercase is rejected so one object never has two spellings, which would otherwise produce two
	/// store entries for identical bytes. Git LFS emits lowercase.
	/// </remarks>
	private static bool IsOid(string segment)
	{
		if (segment.Length != OidLength)
		{
			return false;
		}

		foreach (char character in segment)
		{
			if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
			{
				return false;
			}
		}

		return true;
	}
}
