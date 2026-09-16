// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Locks;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// Handles the lock routes: listing, batched locking, and the relayed changes that invalidate a
/// snapshot.
/// </summary>
/// <remarks>
/// Separate from the object routes because it shares almost nothing with them. The lock routes need
/// the upstream and the allow-list check, both of which the dispatcher has already done by the time
/// one is called, and nothing about transfer tokens, the object store, or public URL resolution.
/// </remarks>
/// <param name="lockLists">Answers lock listings from a snapshot.</param>
/// <param name="lockSnapshots">Holds lock snapshots, so a relayed change can invalidate one.</param>
/// <param name="lockFanOut">Runs the individual calls of a batched lock request.</param>
/// <param name="relay">Passes a request upstream when it cannot be terminated here.</param>
/// <param name="options">The configured options.</param>
internal sealed class LockRouteHandler(
	LockListService lockLists,
	ILockSnapshotStore lockSnapshots,
	LockFanOut lockFanOut,
	UpstreamRelay relay,
	IOptions<GitLfsCacheOptions> options)
{
	/// <summary>
	/// Answers a lock listing, from the snapshot when that is both possible and permitted.
	/// </summary>
	/// <param name="context">The request context.</param>
	/// <param name="route">The parsed route.</param>
	/// <param name="upstreamBase">The resolved upstream base URL.</param>
	/// <param name="cancellationToken">Cancels the listing.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public async Task ListAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(context);
		Ensure.NotNull(route);

		LockSnapshotKey key = new(
			route.Upstream,
			route.RepositoryPath,
			context.Request.Query["refspec"].FirstOrDefault());

		LockListOutcome outcome = await lockLists
			.ResolveAsync(key, upstreamBase, context.Request.Headers.Authorization.ToString(), cancellationToken)
			.ConfigureAwait(false);

		switch (outcome.Kind)
		{
			case LockListOutcomeKind.Refuse:
				// Upstream's own refusal, not a proxy interpretation of it.
				context.Response.StatusCode = (int)outcome.Status!.Value;
				return;

			case LockListOutcomeKind.Serve:
				await WriteLockPageAsync(context, outcome.Snapshot!, cancellationToken).ConfigureAwait(false);
				return;

			default:
				await relay.RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;
		}
	}

	/// <summary>
	/// Runs a batched lock or unlock, issuing the individual calls in parallel.
	/// </summary>
	/// <remarks>
	/// A proxy extension, so it is refused rather than relayed when the subsystem is switched off: an
	/// upstream has no such endpoint, and relaying would turn a disabled feature into a confusing 404
	/// from the forge instead of a clear one from here.
	/// </remarks>
	/// <param name="context">The request context.</param>
	/// <param name="route">The parsed route.</param>
	/// <param name="upstreamBase">The resolved upstream base URL.</param>
	/// <param name="cancellationToken">Cancels the fan-out.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public async Task FanOutAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(context);
		Ensure.NotNull(route);

		if (!options.Value.Locks.Enabled)
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		JsonNode? body;

		try
		{
			body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		}
		catch (System.Text.Json.JsonException)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		if (!LockFanOutRequest.TryParse(body, out LockFanOutRequest? request))
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}

		// Refused outright rather than accepted and throttled part way through, which would leave the
		// caller reconciling a partial result they never asked for.
		if (request.Targets.Count > options.Value.Locks.MaxFanOutPaths)
		{
			context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
			return;
		}

		LockSnapshotKey key = new(route.Upstream, route.RepositoryPath, request.Ref);

		JsonObject results = await lockFanOut
			.ExecuteAsync(
				request,
				key,
				upstreamBase,
				context.Request.Headers.Authorization.ToString(),
				cancellationToken)
			.ConfigureAwait(false);

		// Always 200 when the request itself was well formed. Partial success is the normal outcome,
		// and a transport-level failure would discard the half that worked.
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = UpstreamRequests.LfsMediaType;

		await context.Response
			.WriteAsync(results.ToJsonString(), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Relays a lock creation or release, then drops the snapshot if it took effect.
	/// </summary>
	/// <remarks>
	/// Creation and release are relayed, never terminated, because upstream is the only thing that may
	/// grant or release a lock. The snapshot is dropped afterwards so the change this client just made
	/// is visible to the next listing rather than waiting out the lifetime.
	/// </remarks>
	/// <param name="context">The request context.</param>
	/// <param name="route">The parsed route.</param>
	/// <param name="upstreamBase">The resolved upstream base URL.</param>
	/// <param name="cancellationToken">Cancels the relay.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public async Task RelayChangeAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(context);
		Ensure.NotNull(route);

		await relay.RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
		InvalidateIfChanged(context, route);
	}

	/// <summary>
	/// Writes one page of a snapshot, applying the filters and cursor the client asked for.
	/// </summary>
	private static async Task WriteLockPageAsync(
		HttpContext context,
		LockSnapshot snapshot,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<LockEntry> matches = snapshot.Filter(
			context.Request.Query["path"].FirstOrDefault(),
			context.Request.Query["id"].FirstOrDefault());

		int offset = 0;

		// A cursor from a snapshot that has since been replaced restarts the walk rather than being
		// applied to a different ordering, which would silently skip or repeat locks.
		if (LockCursor.TryDecode(context.Request.Query["cursor"].FirstOrDefault(), out LockCursor? cursor)
			&& cursor.SnapshotId == snapshot.Id)
		{
			offset = cursor.Offset;
		}

		int? limit = int.TryParse(
			context.Request.Query["limit"].FirstOrDefault(),
			System.Globalization.NumberStyles.None,
			System.Globalization.CultureInfo.InvariantCulture,
			out int requested)
			? requested
			: null;

		(IReadOnlyList<LockEntry> page, int? nextOffset) = LockSnapshot.Paginate(matches, offset, limit);

		JsonObject body = LockListParser.BuildResponse(
			page,
			nextOffset is int next ? new LockCursor(snapshot.Id, next).Encode() : null);

		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = UpstreamRequests.LfsMediaType;

		await context.Response
			.WriteAsync(body.ToJsonString(), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Drops the snapshot when a relayed lock change actually took effect.
	/// </summary>
	/// <remarks>
	/// Gated on the response status, because invalidating after a refused creation would throw away a
	/// perfectly good snapshot every time two people raced for the same file, which is exactly when
	/// the cache is under the most load.
	/// </remarks>
	private void InvalidateIfChanged(HttpContext context, LfsRoute route)
	{
		if (context.Response.StatusCode is >= 200 and < 300)
		{
			lockSnapshots.Invalidate(new LockSnapshotKey(
				route.Upstream,
				route.RepositoryPath,
				context.Request.Query["refspec"].FirstOrDefault()));
		}
	}
}
