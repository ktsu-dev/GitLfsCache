// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Root configuration for the caching Git LFS proxy.
/// </summary>
/// <remarks>
/// Collection properties are getter-only with an initializer. The configuration binder populates
/// them in place, and a settable collection property would trip CA2227 under warnings-as-errors.
/// </remarks>
public sealed class GitLfsCacheOptions
{
	/// <summary>
	/// The configuration section these options bind from.
	/// </summary>
	public const string SectionName = "GitLfsCache";

	/// <summary>
	/// Gets or sets the externally reachable base URL used to build rewritten transfer URLs. When
	/// null, the request host and forwarded headers are used instead.
	/// </summary>
	public Uri? PublicBaseUrl { get; set; }

	/// <summary>
	/// Gets the base64 encoded 32 byte token keys. The first is used to encrypt, and every entry is
	/// tried when decrypting, so a key can be rotated without breaking transfers in flight.
	/// </summary>
	public IList<string> TokenKeys { get; } = [];

	/// <summary>
	/// Gets or sets how long a rewritten transfer URL remains valid.
	/// </summary>
	public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

	/// <summary>
	/// Gets or sets the object store settings.
	/// </summary>
	public StoreOptions Store { get; set; } = new();

	/// <summary>
	/// Gets or sets the upstream fetch settings.
	/// </summary>
	public FetchOptions Fetch { get; set; } = new();

	/// <summary>
	/// Gets or sets the locking API settings.
	/// </summary>
	public LocksOptions Locks { get; set; } = new();

	/// <summary>
	/// Gets the configured upstreams, keyed by the first path segment clients address them by.
	/// </summary>
	public IDictionary<string, UpstreamOptions> Upstreams { get; } =
		new Dictionary<string, UpstreamOptions>(StringComparer.OrdinalIgnoreCase);
}
