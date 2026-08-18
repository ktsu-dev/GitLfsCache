// Copyright (c) 2023-2026 ktsu.dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// One configured upstream Git LFS server.
/// </summary>
public sealed class UpstreamOptions
{
	/// <summary>
	/// Gets or sets the absolute base URL of the upstream, for example <c>https://github.com</c>.
	/// </summary>
	/// <remarks>
	/// Typed as <see cref="Uri"/> rather than a string. The configuration binder converts the
	/// configured string, and a relative or wrong-scheme value still reaches the options validator,
	/// which reports it against the setting name it came from.
	/// </remarks>
	public Uri? BaseUrl { get; set; }
}
