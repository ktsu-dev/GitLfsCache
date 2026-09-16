// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Dispatches every request under an upstream prefix to the handler its route selects.
/// </summary>
/// <remarks>
/// The preamble that every route genuinely shares lives here and runs once: parse the path, resolve
/// the upstream, and check the repository against the allow-list. Past that point the routes divide
/// into two groups that share nothing else, so each has its own handler —
/// <see cref="ObjectRouteHandler"/> for batch, transfer and verify, which need transfer tokens and
/// the object store, and <see cref="LockRouteHandler"/> for the lock routes, which need neither.
/// </remarks>
/// <param name="registry">Resolves upstream keys.</param>
/// <param name="allowList">Decides which repository paths an upstream may be used for.</param>
/// <param name="objects">Handles batch, transfer and verify.</param>
/// <param name="locks">Handles the lock routes.</param>
/// <param name="relay">Passes anything the proxy does not model upstream verbatim.</param>
/// <param name="options">The configured options.</param>
/// <param name="logger">Logger.</param>
internal sealed class GitLfsCacheHandler(
	IUpstreamRegistry registry,
	IRepositoryAllowList allowList,
	ObjectRouteHandler objects,
	LockRouteHandler locks,
	UpstreamRelay relay,
	IOptions<GitLfsCacheOptions> options,
	ILogger<GitLfsCacheHandler> logger)
{
	/// <summary>
	/// Dispatches one request.
	/// </summary>
	/// <param name="context">The request context.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public async Task HandleAsync(HttpContext context)
	{
		Ensure.NotNull(context);

		if (!LfsRouteParser.TryParse(context.Request.Path.Value, out LfsRoute? route))
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		if (!registry.TryResolve(route.Upstream, out Uri? resolved) || resolved is null)
		{
			EndpointLog.UnknownUpstream(logger, route.Upstream);
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		// Checked here, once, rather than in each branch below, so batch, transfer, verify and relay
		// are all covered and any route added later inherits it. Before any upstream call, so a
		// refused path costs upstream nothing and cannot be used to learn anything about it. A 404
		// rather than a 403 for the same reason an unknown upstream key is a 404: separating the two
		// would tell a caller which repositories exist.
		if (!allowList.IsAllowed(route.Upstream, route.RelayPath))
		{
			EndpointLog.RepositoryNotAllowed(logger, route.RelayPath, route.Upstream);
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		Uri upstreamBase = resolved;

		CancellationToken cancellationToken = context.RequestAborted;

		// With no store there is nothing to rewrite hrefs towards, so batch, transfer and verify all
		// fall through to the relay below. A metadata-only deployment therefore never sees an object
		// byte: clients receive upstream's own hrefs and go straight there.
		bool caching = options.Value.Store.Enabled;

		switch (route.Kind)
		{
			case LfsRouteKind.Batch when caching && HttpMethods.IsPost(context.Request.Method):
				await objects.BatchAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Transfer when caching && HttpMethods.IsGet(context.Request.Method):
				await objects.DownloadAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Transfer when caching && HttpMethods.IsPut(context.Request.Method):
				await objects.UploadAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Verify when caching && HttpMethods.IsPost(context.Request.Method):
				await objects.VerifyAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Locks when HttpMethods.IsGet(context.Request.Method):
				await locks.ListAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.LocksBatch when HttpMethods.IsPost(context.Request.Method):
				await locks.FanOutAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			// Creation and release are relayed, never terminated, because upstream is the only thing
			// that may grant or release a lock, and the snapshot is dropped afterwards.
			case LfsRouteKind.Locks when HttpMethods.IsPost(context.Request.Method):
			case LfsRouteKind.LocksUnlock when HttpMethods.IsPost(context.Request.Method):
				await locks.RelayChangeAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			default:
				// Includes a recognized path reached with an unexpected method. Relaying rather than
				// rejecting keeps the proxy transparent to anything it does not model.
				await relay.RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;
		}
	}
}
