// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Encodes and decodes the opaque token carried by a rewritten transfer URL.
/// </summary>
public interface IHrefTokenCodec
{
	/// <summary>
	/// Encrypts, authenticates, and URL-safe encodes a token.
	/// </summary>
	/// <param name="token">The payload to encode.</param>
	/// <returns>A URL-safe string suitable for a query parameter value.</returns>
	public string Encode(HrefToken token);

	/// <summary>
	/// Decodes, authenticates, decrypts, and expiry-checks a token.
	/// </summary>
	/// <param name="encoded">The encoded token, or null.</param>
	/// <param name="token">The decoded payload, or null on any failure.</param>
	/// <param name="failureReason">
	/// Why decoding failed, for logging. Never returned to a client, because telling a caller which
	/// part of a token it got wrong helps only the caller who is guessing.
	/// </param>
	/// <returns><see langword="true"/> when the token is authentic and unexpired.</returns>
	public bool TryDecode(
		string? encoded,
		[NotNullWhen(true)] out HrefToken? token,
		out string? failureReason);
}
