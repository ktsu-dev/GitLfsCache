// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

/// <summary>
/// The payload carried inside a rewritten transfer URL.
/// </summary>
/// <remarks>
/// Carrying the upstream action here rather than in server state is what makes replicas stateless,
/// and what lets an object evicted between the batch call and the transfer still resolve.
/// </remarks>
public sealed record HrefToken
{
	/// <summary>Gets the object id, a lowercase hex SHA256 digest.</summary>
	public required string Oid { get; init; }

	/// <summary>Gets the object size in bytes as reported by upstream.</summary>
	public required long Size { get; init; }

	/// <summary>Gets the configured upstream key this object belongs to.</summary>
	public required string Upstream { get; init; }

	/// <summary>Gets the action this token authorizes. See <see cref="TokenAction"/>.</summary>
	public required string Action { get; init; }

	/// <summary>Gets the upstream URL this action was originally pointed at.</summary>
	public required string UpstreamHref { get; init; }

	/// <summary>Gets the headers upstream requires on the transfer, including any credential.</summary>
	public IReadOnlyDictionary<string, string> UpstreamHeaders { get; init; } =
		new Dictionary<string, string>();

	/// <summary>Gets the instant after which this token is refused.</summary>
	public required DateTimeOffset ExpiresAt { get; init; }
}
