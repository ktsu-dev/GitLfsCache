// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache;

using ktsu.GitLfsCache.Endpoints;
using ktsu.GitLfsCache.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// Maps the caching Git LFS proxy onto an endpoint route builder.
/// </summary>
public static class GitLfsCacheEndpointRouteBuilderExtensions
{
	/// <summary>
	/// Maps the health probes and the proxy's catch-all route.
	/// </summary>
	/// <remarks>
	/// One catch-all route rather than a route per Git LFS endpoint, because a repository path has
	/// variable depth and ASP.NET routing only allows a catch-all as the final segment. The dispatch
	/// happens in <see cref="LfsRouteParser"/>, which is directly testable.
	/// </remarks>
	/// <param name="endpoints">The endpoint route builder.</param>
	/// <returns>The same builder, for chaining.</returns>
	public static IEndpointRouteBuilder MapGitLfsCache(this IEndpointRouteBuilder endpoints)
	{
		Ensure.NotNull(endpoints);

		endpoints.MapGet("/healthz", () => Results.Text("ok"))
			.WithName("Liveness");

		endpoints.MapGet("/readyz", (StoreReadiness readiness) => readiness.IsReady
			? Results.Text("ready")
			: Results.Text(
				readiness.FailureReason ?? "The store is not ready.",
				statusCode: StatusCodes.Status503ServiceUnavailable))
			.WithName("Readiness");

		// MapFallback rather than an explicit catch-all route: it matches every method and path the
		// health probes did not, which is precisely the intent, and it avoids declaring route
		// parameters the handler never reads because it parses the path itself.
		endpoints.MapFallback((HttpContext context, GitLfsCacheHandler handler) => handler.HandleAsync(context))
			.WithName("GitLfsCache");

		return endpoints;
	}
}
