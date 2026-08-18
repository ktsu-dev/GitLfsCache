// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Batch;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Fetching;
using ktsu.GitLfsCache.Observability;
using ktsu.GitLfsCache.Storage;
using ktsu.GitLfsCache.Tokens;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Handles every request under an upstream prefix.
/// </summary>
/// <remarks>
/// One handler rather than several because the paths share their front half: resolve the upstream,
/// validate the token, and decide whether the bytes come from the store or from upstream. Splitting
/// that across five endpoint classes would mean five copies of the same preamble.
/// </remarks>
/// <param name="registry">Resolves upstream keys.</param>
/// <param name="upstreamClient">Sends requests upstream.</param>
/// <param name="codec">Decodes transfer tokens.</param>
/// <param name="rewriter">Rewrites batch responses.</param>
/// <param name="store">The local object store.</param>
/// <param name="coalescer">Keeps concurrent misses to one upstream fetch.</param>
/// <param name="publicUrls">Resolves the base URL rewritten hrefs point at.</param>
/// <param name="metrics">Cache counters.</param>
/// <param name="options">The configured options.</param>
/// <param name="logger">Logger.</param>
public sealed class GitLfsCacheHandler(
	IUpstreamRegistry registry,
	IUpstreamClient upstreamClient,
	IHrefTokenCodec codec,
	BatchRewriter rewriter,
	IObjectStore store,
	IFetchCoalescer coalescer,
	PublicUrlResolver publicUrls,
	CacheMetrics metrics,
	IOptions<GitLfsCacheOptions> options,
	ILogger<GitLfsCacheHandler> logger)
{
	private const string OctetStream = "application/octet-stream";
	private const string TokenQueryParameter = "t";

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

		Uri upstreamBase = resolved;

		CancellationToken cancellationToken = context.RequestAborted;

		switch (route.Kind)
		{
			case LfsRouteKind.Batch when HttpMethods.IsPost(context.Request.Method):
				await BatchAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Transfer when HttpMethods.IsGet(context.Request.Method):
				await DownloadAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Transfer when HttpMethods.IsPut(context.Request.Method):
				await UploadAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			case LfsRouteKind.Verify when HttpMethods.IsPost(context.Request.Method):
				await VerifyAsync(context, route, cancellationToken).ConfigureAwait(false);
				return;

			default:
				// Includes a recognized path reached with an unexpected method. Relaying rather than
				// rejecting keeps the proxy transparent to anything it does not model.
				await RelayAsync(context, route, upstreamBase, cancellationToken).ConfigureAwait(false);
				return;
		}
	}

	private async Task BatchAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			upstreamBase,
			route.RepositoryPath,
			context.Request.Body,
			context.Request.Headers.Authorization.ToString());

		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		// Upstream is the authority on access. A refusal is relayed exactly as it arrived, so the
		// client sees upstream's real answer rather than a proxy interpretation of it.
		if (!response.IsSuccessStatusCode)
		{
			await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
			return;
		}

		JsonNode? upstreamBody;

		Stream batchBody = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);

		await using (batchBody.ConfigureAwait(false))
		{
			upstreamBody = await JsonNode.ParseAsync(batchBody, cancellationToken: cancellationToken)
				.ConfigureAwait(false);
		}

		if (upstreamBody is null)
		{
			context.Response.StatusCode = StatusCodes.Status502BadGateway;
			return;
		}

		JsonNode rewritten = rewriter.Rewrite(upstreamBody, new BatchRewriteContext
		{
			Upstream = route.Upstream,
			RepositoryPath = route.RepositoryPath,
			PublicBaseUrl = publicUrls.Resolve(context.Request),
		});

		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = UpstreamRequests.LfsMediaType;
		await context.Response
			.WriteAsync(rewritten.ToJsonString(), cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task DownloadAsync(HttpContext context, LfsRoute route, CancellationToken cancellationToken)
	{
		if (!TryGetToken(context, route, TokenAction.Download, out HrefToken? token))
		{
			return;
		}

		string range = context.Request.Headers.Range.ToString();

		Stream? cached = store.OpenRead(route.Upstream, token.Oid, out long length);

		if (cached is not null)
		{
			await using (cached.ConfigureAwait(false))
			{
				store.Touch(route.Upstream, token.Oid);
				metrics.RecordHit(route.Upstream, length);
				EndpointLog.ServedFromCache(logger, token.Oid, route.Upstream);

				await ServeFromStoreAsync(context, cached, length, cancellationToken).ConfigureAwait(false);
			}

			return;
		}

		if (!string.IsNullOrEmpty(range))
		{
			// A partial response cannot be stored as a whole object, so the range is forwarded and the
			// result streamed straight through. Rare enough not to be worth partial-object bookkeeping.
			EndpointLog.RangeRequestNotStored(logger, token.Oid);
			await StreamFromUpstreamAsync(context, route, token, range, storeLocally: false, cancellationToken)
				.ConfigureAwait(false);
			return;
		}

		using IFetchTicket ticket = coalescer.Acquire(route.Upstream, token.Oid);

		if (!ticket.IsLeader)
		{
			metrics.RecordCoalescedWait(route.Upstream);
			EndpointLog.WaitingForLeader(logger, token.Oid, route.Upstream);

			bool published = await ticket
				.WaitForLeaderAsync(options.Value.Fetch.FollowerTimeout, cancellationToken)
				.ConfigureAwait(false);

			long nowLength = 0;
			Stream? nowCached = published
				? store.OpenRead(route.Upstream, token.Oid, out nowLength)
				: null;

			if (nowCached is not null)
			{
				await using (nowCached.ConfigureAwait(false))
				{
					store.Touch(route.Upstream, token.Oid);
					metrics.RecordHit(route.Upstream, nowLength);
					await ServeFromStoreAsync(context, nowCached, nowLength, cancellationToken)
						.ConfigureAwait(false);
				}

				return;
			}

			EndpointLog.LeaderDidNotFinish(logger, token.Oid);
		}

		bool stored = await StreamFromUpstreamAsync(
			context,
			route,
			token,
			range: null,
			storeLocally: true,
			cancellationToken).ConfigureAwait(false);

		if (ticket.IsLeader)
		{
			ticket.Complete(stored);
		}
	}

	private async Task<bool> StreamFromUpstreamAsync(
		HttpContext context,
		LfsRoute route,
		HrefToken token,
		string? range,
		bool storeLocally,
		CancellationToken cancellationToken)
	{
		EndpointLog.FetchingUpstream(logger, token.Oid, route.Upstream);

		using HttpRequestMessage request = UpstreamRequests.BuildObjectRequest(token, range);
		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			EndpointLog.UpstreamRefusedTransfer(logger, (int)response.StatusCode, token.Oid);
			await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
			return false;
		}

		CopyTransferHeaders(response, context);

		Stream upstreamBody = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);

		await using ConfiguredAsyncDisposable upstreamBodyDisposal = upstreamBody.ConfigureAwait(false);

		if (!storeLocally)
		{
			long streamed = await StreamTee
				.CopyAsync(upstreamBody, context.Response.Body, null, null, cancellationToken)
				.ConfigureAwait(false);

			metrics.RecordMiss(route.Upstream, streamed);
			return false;
		}

		StagingHandle staging = store.OpenStaging(route.Upstream);

		await using (staging.ConfigureAwait(false))
		{
			long streamed = await StreamTee.CopyAsync(
				upstreamBody,
				context.Response.Body,
				staging.Stream,
				failure => EndpointLog.StoreSinkFailed(logger, failure, token.Oid),
				cancellationToken).ConfigureAwait(false);

			metrics.RecordMiss(route.Upstream, streamed);

			bool published = await store
				.PublishAsync(staging, route.Upstream, token.Oid, cancellationToken)
				.ConfigureAwait(false);

			if (published)
			{
				metrics.RecordStored(route.Upstream);
			}
			else
			{
				metrics.RecordVerificationFailure(route.Upstream);
			}

			return published;
		}
	}

	private async Task UploadAsync(HttpContext context, LfsRoute route, CancellationToken cancellationToken)
	{
		if (!TryGetToken(context, route, TokenAction.Upload, out HrefToken? token))
		{
			return;
		}

		StagingHandle staging = store.OpenStaging(route.Upstream);

		await using (staging.ConfigureAwait(false))
		{
			ReadTeeStream teed = new(
				context.Request.Body,
				staging.Stream,
				failure => EndpointLog.StoreSinkFailed(logger, failure, token.Oid));

			await using ConfiguredAsyncDisposable teedDisposal = teed.ConfigureAwait(false);

			using HttpRequestMessage request = UpstreamRequests.BuildUploadRequest(
				token,
				teed,
				context.Request.ContentLength);

			using HttpResponseMessage response = await upstreamClient
				.SendAsync(request, cancellationToken)
				.ConfigureAwait(false);

			metrics.RecordUpload(route.Upstream, teed.BytesRead);

			// The object is published only after upstream accepts it. Caching an upload upstream
			// rejected would serve bytes no one can verify against the real remote.
			if (response.IsSuccessStatusCode && teed.SinkIsLive)
			{
				if (await store.PublishAsync(staging, route.Upstream, token.Oid, cancellationToken)
					.ConfigureAwait(false))
				{
					metrics.RecordStored(route.Upstream);
				}
				else
				{
					metrics.RecordVerificationFailure(route.Upstream);
				}
			}

			await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task VerifyAsync(HttpContext context, LfsRoute route, CancellationToken cancellationToken)
	{
		if (!TryGetToken(context, route, TokenAction.Verify, out HrefToken? token))
		{
			return;
		}

		using HttpRequestMessage request = UpstreamRequests.BuildVerifyRequest(token, context.Request.Body);
		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
	}

	private async Task RelayAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers = context.Request.Headers
			.Select(header => new KeyValuePair<string, IEnumerable<string>>(
				header.Key,
				header.Value.Where(value => value is not null).Select(value => value!)));

		using HttpRequestMessage request = UpstreamRequests.BuildRelayRequest(
			upstreamBase,
			context.Request.Method,
			route.RelayPath,
			context.Request.QueryString.Value ?? string.Empty,
			context.Request.Body,
			headers);

		using HttpResponseMessage response = await upstreamClient
			.SendAsync(request, cancellationToken)
			.ConfigureAwait(false);

		EndpointLog.Relayed(logger, context.Request.Method, route.RelayPath, route.Upstream);
		await CopyResponseAsync(response, context, cancellationToken).ConfigureAwait(false);
	}

	private bool TryGetToken(
		HttpContext context,
		LfsRoute route,
		string expectedAction,
		[NotNullWhen(true)] out HrefToken? token)
	{
		token = null;
		string? encoded = context.Request.Query[TokenQueryParameter];

		if (!codec.TryDecode(encoded, out HrefToken? decoded, out string? failureReason))
		{
			metrics.RecordRejectedToken();
			EndpointLog.RejectedToken(logger, route.Oid ?? "(none)", failureReason ?? "unspecified");

			// No detail in the response: telling a caller which part of a token it got wrong only
			// helps a caller who is guessing.
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}

		if (decoded.Action != expectedAction)
		{
			metrics.RecordRejectedToken();
			EndpointLog.TokenActionMismatch(logger, decoded.Action, expectedAction);
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}

		// A token is bound to one object, so it cannot be replayed against a different path.
		if (decoded.Oid != route.Oid || decoded.Upstream != route.Upstream)
		{
			metrics.RecordRejectedToken();
			EndpointLog.RejectedToken(logger, route.Oid ?? "(none)", "token does not match the requested object");
			context.Response.StatusCode = StatusCodes.Status403Forbidden;
			return false;
		}

		token = decoded;
		return true;
	}

	private static async Task ServeFromStoreAsync(
		HttpContext context,
		Stream cached,
		long length,
		CancellationToken cancellationToken)
	{
		context.Response.ContentType = OctetStream;
		context.Response.ContentLength = length;

		await StreamTee
			.CopyAsync(cached, context.Response.Body, null, null, cancellationToken)
			.ConfigureAwait(false);
	}

	private static void CopyTransferHeaders(HttpResponseMessage response, HttpContext context)
	{
		context.Response.StatusCode = (int)response.StatusCode;
		context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? OctetStream;

		if (response.Content.Headers.ContentLength is long length)
		{
			context.Response.ContentLength = length;
		}

		if (response.Content.Headers.ContentRange is not null)
		{
			context.Response.Headers.ContentRange = response.Content.Headers.ContentRange.ToString();
		}

		if (response.Headers.AcceptRanges.Count > 0)
		{
			context.Response.Headers.AcceptRanges = string.Join(", ", response.Headers.AcceptRanges);
		}
	}

	private static async Task CopyResponseAsync(
		HttpResponseMessage response,
		HttpContext context,
		CancellationToken cancellationToken)
	{
		context.Response.StatusCode = (int)response.StatusCode;

		foreach ((string name, IEnumerable<string> values) in response.Headers)
		{
			if (!UpstreamRequests.IsHopHeader(name))
			{
				context.Response.Headers[name] = values.ToArray();
			}
		}

		foreach ((string name, IEnumerable<string> values) in response.Content.Headers)
		{
			if (!UpstreamRequests.IsHopHeader(name))
			{
				context.Response.Headers[name] = values.ToArray();
			}
		}

		if (response.StatusCode == HttpStatusCode.NoContent)
		{
			return;
		}

		Stream body = await response.Content
			.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);

		await using (body.ConfigureAwait(false))
		{
			await body.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
		}
	}
}
