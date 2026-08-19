// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

/// <summary>
/// Reads and writes the listing bodies of the Git LFS locking API.
/// </summary>
/// <remarks>
/// Pure, so the shape of every page the proxy assembles and every response it returns is testable
/// without a socket or a store.
/// </remarks>
public static class LockListParser
{
	/// <summary>The array of locks in a listing body.</summary>
	public const string LocksProperty = "locks";

	/// <summary>The continuation token in a listing body.</summary>
	public const string NextCursorProperty = "next_cursor";

	private const string IdProperty = "id";
	private const string PathProperty = "path";

	/// <summary>
	/// Reads one page of a listing as upstream returned it.
	/// </summary>
	/// <remarks>
	/// A page containing an entry without an id or a path is rejected rather than having that entry
	/// skipped. A lock silently dropped from the listing reads to a client as a file nobody holds,
	/// which is the one wrong answer this cache must never produce, so a body that cannot be
	/// understood has to fail loudly and fall back to relaying.
	/// </remarks>
	/// <param name="body">The parsed response body.</param>
	/// <param name="entries">The locks on this page.</param>
	/// <param name="nextCursor">The cursor for the following page, or null when this is the last.</param>
	/// <returns><see langword="true"/> when the body was a well formed listing page.</returns>
	public static bool TryParsePage(
		JsonNode? body,
		[NotNullWhen(true)] out IReadOnlyList<LockEntry>? entries,
		out string? nextCursor)
	{
		entries = null;
		nextCursor = null;

		if (body is not JsonObject page)
		{
			return false;
		}

		// A body with no locks array at all is a well formed empty page. Forges differ on whether they
		// send an empty array or omit the property, and refusing one of those spellings would make the
		// cache fall back to relaying against a repository that simply has no locks.
		JsonArray locks = page[LocksProperty] is JsonArray array ? array : [];

		List<LockEntry> parsed = [];

		foreach (JsonNode? element in locks)
		{
			if (element is not JsonObject entry)
			{
				return false;
			}

			string? id = entry[IdProperty]?.GetValue<string>();
			string? path = entry[PathProperty]?.GetValue<string>();

			if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path))
			{
				return false;
			}

			// Deep cloned so a snapshot cannot be changed later through the tree it was parsed from,
			// and so one entry can be placed into many response bodies: a JsonNode has a parent and
			// cannot be attached twice.
			parsed.Add(new LockEntry(id, path, (JsonObject)entry.DeepClone()));
		}

		entries = parsed;
		nextCursor = page[NextCursorProperty]?.GetValue<string>() is string cursor && cursor.Length > 0
			? cursor
			: null;

		return true;
	}

	/// <summary>
	/// Builds a listing body to return to a client.
	/// </summary>
	/// <param name="entries">The locks on this page.</param>
	/// <param name="nextCursor">The cursor for the following page, or null when this is the last.</param>
	/// <returns>The response body.</returns>
	public static JsonObject BuildResponse(IReadOnlyList<LockEntry> entries, string? nextCursor)
	{
		Ensure.NotNull(entries);

		JsonArray locks = [];

		foreach (LockEntry entry in entries)
		{
			locks.Add(entry.Payload.DeepClone());
		}

		JsonObject body = new() { [LocksProperty] = locks };

		// Omitted rather than sent as null on the last page. A client testing for the property's
		// presence and one testing its value should both conclude the walk is over.
		if (nextCursor is not null)
		{
			body[NextCursorProperty] = nextCursor;
		}

		return body;
	}
}
