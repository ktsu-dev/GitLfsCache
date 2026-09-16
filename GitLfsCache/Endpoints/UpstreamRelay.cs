// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Endpoints;

using System.Net;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Passes a request upstream verbatim and copies the answer back.
/// </summary>
/// <remarks>
/// Shared rather than duplicated because both route groups end up here: the object routes when the
/// store is off or the method is one the proxy does not model, and the lock routes because creation
/// and release are upstream's alone to grant.
/// </remarks>
/// <param name="upstreamClient">Sends requests upstream.</param>
/// <param name="logger">Logger.</param>
internal sealed class UpstreamRelay(IUpstreamClient upstreamClient, ILogger<UpstreamRelay> logger)
{
	/// <summary>
	/// Relays one request and writes upstream's answer to the response unchanged.
	/// </summary>
	/// <param name="context">The request context.</param>
	/// <param name="route">The parsed route.</param>
	/// <param name="upstreamBase">The resolved upstream base URL.</param>
	/// <param name="cancellationToken">Cancels the relay.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public async Task RelayAsync(
		HttpContext context,
		LfsRoute route,
		Uri upstreamBase,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(context);
		Ensure.NotNull(route);

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

	/// <summary>
	/// Copies an upstream response onto the outgoing response, minus the hop-by-hop headers.
	/// </summary>
	/// <remarks>
	/// Static and shared because every route that does not terminate a request itself ends by handing
	/// upstream's own answer back, and they must all drop the same headers to do it.
	/// </remarks>
	/// <param name="response">The upstream response.</param>
	/// <param name="context">The request context to write to.</param>
	/// <param name="cancellationToken">Cancels the copy.</param>
	/// <returns>A task that completes when the response has been written.</returns>
	public static async Task CopyResponseAsync(
		HttpResponseMessage response,
		HttpContext context,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(response);
		Ensure.NotNull(context);

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
