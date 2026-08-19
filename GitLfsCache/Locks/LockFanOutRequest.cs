// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

/// <summary>
/// A batched lock or unlock, as a client asked for it.
/// </summary>
/// <remarks>
/// A proxy extension rather than a specification endpoint. It exists because git-lfs turns each path
/// into its own request and issues them one after another, so locking five hundred assets over a
/// wide-area link is five hundred sequential round trips. Fanning out from a host adjacent to the
/// forge turns that into one round trip for the client.
/// </remarks>
/// <param name="Operation">Whether this locks or unlocks.</param>
/// <param name="Targets">The paths to lock, or the paths and ids to unlock.</param>
/// <param name="Ref">The ref to pass through to upstream, or null when none was given.</param>
/// <param name="Force">Whether a release should be forced.</param>
public sealed record LockFanOutRequest(
	LockFanOutOperation Operation,
	IReadOnlyList<LockFanOutTarget> Targets,
	string? Ref,
	bool Force)
{
	/// <summary>
	/// Reads a batched request body.
	/// </summary>
	/// <remarks>
	/// Every malformed shape is refused rather than partly honoured. A body that half parses would
	/// produce a fan-out over whichever half was understood, and the client would have no way to tell
	/// that from a fan-out where the rest simply failed.
	/// </remarks>
	/// <param name="body">The parsed request body.</param>
	/// <param name="request">The parsed request.</param>
	/// <returns><see langword="true"/> when the body was well formed.</returns>
	public static bool TryParse(JsonNode? body, [NotNullWhen(true)] out LockFanOutRequest? request)
	{
		request = null;

		if (body is not JsonObject root)
		{
			return false;
		}

		LockFanOutOperation operation = ReadOperation(root);

		if (operation == LockFanOutOperation.Unknown)
		{
			return false;
		}

		if (!TryReadTargets(root, operation, out List<LockFanOutTarget>? targets))
		{
			return false;
		}

		request = new LockFanOutRequest(
			operation,
			targets,
			JsonValues.String(root["ref"]?["name"]),
			JsonValues.Bool(root["force"]) ?? false);

		return true;
	}

	private static LockFanOutOperation ReadOperation(JsonObject root) =>
		JsonValues.String(root["operation"]) switch
		{
			"lock" => LockFanOutOperation.Lock,
			"unlock" => LockFanOutOperation.Unlock,
			_ => LockFanOutOperation.Unknown,
		};

	/// <summary>
	/// Reads the paths, and for a release the ids as well.
	/// </summary>
	/// <remarks>
	/// A request naming nothing is refused rather than treated as an empty fan-out, because a client
	/// that meant to send paths and sent none should hear about it rather than get a cheerful empty
	/// result array.
	/// </remarks>
	private static bool TryReadTargets(
		JsonObject root,
		LockFanOutOperation operation,
		[NotNullWhen(true)] out List<LockFanOutTarget>? targets)
	{
		targets = null;
		List<LockFanOutTarget> read = [];

		if (!TryReadStrings(root["paths"], out List<string>? paths))
		{
			return false;
		}

		read.AddRange(paths.Select(path => new LockFanOutTarget(path, null)));

		if (operation == LockFanOutOperation.Unlock)
		{
			if (!TryReadStrings(root["ids"], out List<string>? ids))
			{
				return false;
			}

			read.AddRange(ids.Select(id => new LockFanOutTarget(null, id)));
		}

		if (read.Count == 0)
		{
			return false;
		}

		targets = read;
		return true;
	}

	/// <summary>
	/// Reads an array of non-empty strings, treating an absent array as an empty one.
	/// </summary>
	/// <remarks>
	/// A node that is present but not an array, or an element that is not a non-empty string, refuses
	/// the whole body. A body that half parses would fan out over whichever half was understood, and
	/// the client could not tell that from a fan-out whose other half simply failed.
	/// </remarks>
	private static bool TryReadStrings(JsonNode? node, [NotNullWhen(true)] out List<string>? values)
	{
		values = null;

		if (node is null)
		{
			values = [];
			return true;
		}

		if (node is not JsonArray array)
		{
			return false;
		}

		List<string> read = [];

		foreach (JsonNode? element in array)
		{
			if (JsonValues.String(element) is not string value || value.Length == 0)
			{
				return false;
			}

			read.Add(value);
		}

		values = read;
		return true;
	}
}
