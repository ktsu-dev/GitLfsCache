// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

/// <summary>
/// The Git LFS batch action a token authorizes.
/// </summary>
/// <remarks>
/// A token names its action so a download token cannot be replayed against the upload endpoint,
/// which matters because both share the <c>/objects/{oid}</c> path and differ only by HTTP method.
/// </remarks>
public static class TokenAction
{
	/// <summary>Fetching an object.</summary>
	public const string Download = "download";

	/// <summary>Sending an object.</summary>
	public const string Upload = "upload";

	/// <summary>Confirming an upload landed.</summary>
	public const string Verify = "verify";
}
