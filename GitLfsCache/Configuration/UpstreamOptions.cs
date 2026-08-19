// Copyright (c) 2023-2026 ktsu-dev contributors

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

	/// <summary>
	/// Gets the patterns matching the repository paths this upstream may be used for.
	/// </summary>
	/// <remarks>
	/// Required, with no default and no empty-means-everything behaviour, so that a deployment which
	/// caches whatever it is pointed at has to be asked for rather than arrived at. Write
	/// <c>**</c> to allow every path.
	/// <para>
	/// A pattern matches the whole path following the upstream key, so it normally ends in
	/// <c>**</c>: <c>studio/**</c> allows every route under every repository beginning
	/// <c>studio/</c>, while <c>studio/*</c> would allow only a single further segment and therefore
	/// no real Git LFS path at all.
	/// </para>
	/// <para>
	/// Getter-only with an initializer for the same reason as the collections on
	/// <see cref="GitLfsCacheOptions"/>: the binder populates in place, and a settable collection
	/// property trips CA2227 under warnings-as-errors.
	/// </para>
	/// </remarks>
	public IList<string> Repositories { get; } = [];
}
