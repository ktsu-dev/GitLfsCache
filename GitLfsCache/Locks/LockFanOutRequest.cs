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

		string? operation = JsonValues.String(root["operation"]);

		LockFanOutOperation parsed = operation switch
		{
			"lock" => LockFanOutOperation.Lock,
			"unlock" => LockFanOutOperation.Unlock,
			_ => LockFanOutOperation.Unknown,
		};

		if (parsed == LockFanOutOperation.Unknown)
		{
			return false;
		}

		List<LockFanOutTarget> targets = [];

		if (root["paths"] is JsonArray paths)
		{
			foreach (JsonNode? element in paths)
			{
				if (JsonValues.String(element) is not string path || path.Length == 0)
				{
					return false;
				}

				targets.Add(new LockFanOutTarget(path, null));
			}
		}

		if (parsed == LockFanOutOperation.Unlock && root["ids"] is JsonArray ids)
		{
			foreach (JsonNode? element in ids)
			{
				if (JsonValues.String(element) is not string id || id.Length == 0)
				{
					return false;
				}

				targets.Add(new LockFanOutTarget(null, id));
			}
		}

		if (targets.Count == 0)
		{
			return false;
		}

		request = new LockFanOutRequest(
			parsed,
			targets,
			JsonValues.String(root["ref"]?["name"]),
			JsonValues.Bool(root["force"]) ?? false);

		return true;
	}
}
