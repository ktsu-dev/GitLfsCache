// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Batch;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tokens;
using Microsoft.Extensions.Options;

/// <summary>
/// Rewrites the transfer URLs in an upstream batch response to point at this proxy.
/// </summary>
/// <remarks>
/// The transform works on a node tree rather than typed models so that properties this proxy does
/// not know about survive. The Git LFS batch response is extended in practice, by the specification
/// itself (<c>hash_algo</c>, <c>authenticated</c>) and by individual forges, and a proxy that
/// silently drops what it cannot name is a proxy that breaks clients unpredictably.
/// </remarks>
/// <param name="codec">The token codec.</param>
/// <param name="options">The configured options, supplying the token lifetime.</param>
/// <param name="timeProvider">Clock, injected so token expiry is testable.</param>
public sealed class BatchRewriter(
	IHrefTokenCodec codec,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider)
{
	/// <summary>The action names this proxy terminates locally, in a fixed order.</summary>
	private static readonly string[] RewrittenActions =
		[TokenAction.Download, TokenAction.Upload, TokenAction.Verify];

	/// <summary>
	/// Rewrites a batch response, leaving the input untouched.
	/// </summary>
	/// <param name="upstreamResponse">The parsed upstream response.</param>
	/// <param name="context">The request context.</param>
	/// <returns>A new node tree with rewritten hrefs.</returns>
	public JsonNode Rewrite(JsonNode upstreamResponse, BatchRewriteContext context)
	{
		Ensure.NotNull(upstreamResponse);
		Ensure.NotNull(context);

		// Deep copy so the caller keeps an unmodified original, which the relay path relies on when
		// it decides to pass a response through instead.
		JsonNode rewritten = upstreamResponse.DeepClone();

		if (rewritten is not JsonObject root
			|| !root.TryGetPropertyValue("objects", out JsonNode? objectsNode)
			|| objectsNode is not JsonArray objects)
		{
			return rewritten;
		}

		TimeSpan lifetime = options.Value.TokenLifetime;
		DateTimeOffset expiresAt = timeProvider.GetUtcNow().Add(lifetime);

		foreach (JsonNode? entry in objects)
		{
			if (entry is JsonObject batchObject)
			{
				RewriteObject(batchObject, context, expiresAt, (int)lifetime.TotalSeconds);
			}
		}

		return rewritten;
	}

	private void RewriteObject(
		JsonObject batchObject,
		BatchRewriteContext context,
		DateTimeOffset expiresAt,
		int expiresInSeconds)
	{
		if (!batchObject.TryGetPropertyValue("actions", out JsonNode? actionsNode)
			|| actionsNode is not JsonObject actions)
		{
			// Either an object upstream reported an error for, or one already present upstream that
			// needs no transfer. Both pass through untouched.
			return;
		}

		string? oid = batchObject["oid"]?.GetValue<string>();

		if (string.IsNullOrEmpty(oid))
		{
			return;
		}

		long size = batchObject["size"]?.GetValue<long>() ?? 0;

		foreach (string actionName in RewrittenActions)
		{
			if (actions.TryGetPropertyValue(actionName, out JsonNode? actionNode)
				&& actionNode is JsonObject action)
			{
				RewriteAction(action, actionName, oid, size, context, expiresAt, expiresInSeconds);
			}
		}
	}

	private void RewriteAction(
		JsonObject action,
		string actionName,
		string oid,
		long size,
		BatchRewriteContext context,
		DateTimeOffset expiresAt,
		int expiresInSeconds)
	{
		string? upstreamHref = action["href"]?.GetValue<string>();

		if (string.IsNullOrEmpty(upstreamHref))
		{
			return;
		}

		Dictionary<string, string> headers = [];

		if (action["header"] is JsonObject headerObject)
		{
			foreach ((string name, JsonNode? value) in headerObject)
			{
				if (value is not null)
				{
					headers[name] = value.GetValue<string>();
				}
			}
		}

		string token = codec.Encode(new HrefToken
		{
			Oid = oid,
			Size = size,
			Upstream = context.Upstream,
			Action = actionName,
			UpstreamHref = upstreamHref,
			UpstreamHeaders = headers,
			ExpiresAt = expiresAt,
		});

		action["href"] = BuildProxyHref(actionName, oid, token, context);

		// The credential now lives inside the token. Leaving it here would hand every client the
		// upstream's bearer token, which is most of the point of terminating the transfer locally.
		action.Remove("header");

		// An upstream expires_at would contradict this proxy's own token lifetime, so it is replaced
		// rather than left to disagree.
		action.Remove("expires_at");
		action["expires_in"] = expiresInSeconds;
	}

	private static string BuildProxyHref(
		string actionName,
		string oid,
		string token,
		BatchRewriteContext context)
	{
		// Verify is a distinct route because it is a POST with a JSON body, not an object transfer.
		string suffix = actionName == TokenAction.Verify ? "/verify" : string.Empty;
		string basePath = context.PublicBaseUrl.ToString().TrimEnd('/');

		return $"{basePath}/{context.Upstream}/{context.RepositoryPath}/objects/{oid}{suffix}?t={token}";
	}
}
