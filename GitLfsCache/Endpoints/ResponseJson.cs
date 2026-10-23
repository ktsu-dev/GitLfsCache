// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// Writes the JSON this proxy answers clients with.
/// </summary>
/// <remarks>
/// The default encoder escapes characters that are only dangerous inside HTML, the plus sign among
/// them, and git-lfs parses a lock's <c>locked_at</c> from the raw JSON bytes without unescaping
/// them. Azure DevOps writes a new lock's time with a <c>+00:00</c> offset, so the default turns a
/// time git-lfs can read into one it cannot, and every listing that includes the lock fails. These
/// bodies are served as Git LFS JSON and never embedded in a page, so the relaxed encoder is safe.
/// </remarks>
internal static class ResponseJson
{
	private static readonly JsonSerializerOptions Options = new()
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>
	/// Serializes a response body.
	/// </summary>
	/// <param name="node">The body.</param>
	/// <returns>The JSON text.</returns>
	public static string Write(JsonNode node)
	{
		Ensure.NotNull(node);
		return node.ToJsonString(Options);
	}
}
