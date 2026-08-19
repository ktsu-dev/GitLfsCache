// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Reads values out of untrusted JSON without throwing on the wrong type.
/// </summary>
/// <remarks>
/// <see cref="JsonNode.GetValue{T}"/> throws when the node holds a different kind, so calling it on a
/// body that arrived over the network turns a malformed request into an unhandled exception and a
/// 500. Every read of a client's or an upstream's JSON goes through here instead, so the wrong type
/// is simply an absent value and the caller's existing "this body is malformed" path handles it.
/// </remarks>
internal static class JsonValues
{
	/// <summary>
	/// Reads a string, or null when the node is absent or holds anything else.
	/// </summary>
	/// <param name="node">The node to read.</param>
	/// <returns>The string, or null.</returns>
	public static string? String(JsonNode? node) =>
		node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

	/// <summary>
	/// Reads a boolean, or null when the node is absent or holds anything else.
	/// </summary>
	/// <param name="node">The node to read.</param>
	/// <returns>The boolean, or null.</returns>
	public static bool? Bool(JsonNode? node) => node?.GetValueKind() switch
	{
		JsonValueKind.True => true,
		JsonValueKind.False => false,
		_ => null,
	};
}
