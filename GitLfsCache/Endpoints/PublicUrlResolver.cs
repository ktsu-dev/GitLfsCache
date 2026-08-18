// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using ktsu.GitLfsCache.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// Works out the base URL that rewritten transfer URLs should point at.
/// </summary>
/// <remarks>
/// An explicit <see cref="GitLfsCacheOptions.PublicBaseUrl"/> always wins, because behind an ingress
/// the request the proxy sees and the URL the client used are different things and only the operator
/// knows the latter for certain. Falling back to the request means the deployment must run the
/// forwarded-headers middleware, otherwise a client behind TLS termination receives <c>http</c> hrefs
/// it cannot use.
/// </remarks>
/// <param name="options">The configured options.</param>
public sealed class PublicUrlResolver(IOptions<GitLfsCacheOptions> options)
{
	/// <summary>
	/// Resolves the base URL for the current request.
	/// </summary>
	/// <param name="request">The incoming request.</param>
	/// <returns>The base URL, with no trailing slash.</returns>
	public Uri Resolve(HttpRequest request)
	{
		Ensure.NotNull(request);

		if (options.Value.PublicBaseUrl is Uri configured)
		{
			return configured;
		}

		return new Uri($"{request.Scheme}://{request.Host}{request.PathBase}");
	}
}
