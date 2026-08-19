// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

using System.Globalization;
using System.Net.Http.Headers;
using ktsu.GitLfsCache.Tokens;

/// <summary>
/// Builds the upstream requests the proxy sends, keeping URL and header assembly testable.
/// </summary>
/// <remarks>
/// Pure construction, no I/O. The rules encoded here are the ones that break clients when they are
/// wrong: which headers are forwarded, which are dropped, and how a repository path is joined to an
/// upstream base URL.
/// </remarks>
public static class UpstreamRequests
{
	/// <summary>The media type Git LFS uses for the batch API.</summary>
	public const string LfsMediaType = "application/vnd.git-lfs+json";

	/// <summary>
	/// Headers that must never be forwarded, because they describe the hop rather than the request.
	/// </summary>
	/// <remarks>
	/// Host would point the upstream at the proxy's own name. The transfer-encoding and connection
	/// family are per-connection concerns that HttpClient sets for itself, and forwarding a
	/// Content-Length alongside a streamed body produces a request that contradicts itself.
	/// </remarks>
	private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
	{
		"Host",
		"Connection",
		"Keep-Alive",
		"Proxy-Authenticate",
		"Proxy-Authorization",
		"TE",
		"Trailer",
		"Transfer-Encoding",
		"Upgrade",
		"Content-Length",
	};

	/// <summary>
	/// Builds the batch request that is relayed upstream on every batch call.
	/// </summary>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="repositoryPath">
	/// The path between the upstream key and <c>/objects/batch</c>, for example
	/// <c>owner/repo.git/info/lfs</c>.
	/// </param>
	/// <param name="body">The client's request body, streamed rather than buffered.</param>
	/// <param name="authorization">The client's Authorization header, forwarded unchanged.</param>
	/// <returns>The request to send upstream.</returns>
	public static HttpRequestMessage BuildBatchRequest(
		Uri upstreamBase,
		string repositoryPath,
		Stream body,
		string? authorization)
	{
		Ensure.NotNull(upstreamBase);
		Ensure.NotNull(body);
		Ensure.NotNull(repositoryPath);

		HttpRequestMessage request = new(HttpMethod.Post, Combine(upstreamBase, $"{repositoryPath}/objects/batch"))
		{
			Content = new StreamContent(body),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue(LfsMediaType);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LfsMediaType));
		ApplyAuthorization(request, authorization);

		return request;
	}

	/// <summary>
	/// Builds one page request of a lock listing walk.
	/// </summary>
	/// <remarks>
	/// The client's own Authorization header is forwarded, exactly as on a batch call, because the
	/// walk is performed on behalf of whichever client triggered it and upstream remains the authority
	/// on whether that client may read these locks.
	/// </remarks>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="repositoryPath">The path between the upstream key and <c>/locks</c>.</param>
	/// <param name="cursor">Upstream's cursor for the page to fetch, or null for the first.</param>
	/// <param name="limit">A page size to request, or null to let upstream choose.</param>
	/// <param name="authorization">The client's Authorization header, forwarded unchanged.</param>
	/// <returns>The request to send upstream.</returns>
	public static HttpRequestMessage BuildLockListRequest(
		Uri upstreamBase,
		string repositoryPath,
		string? cursor,
		int? limit,
		string? authorization)
	{
		Ensure.NotNull(upstreamBase);
		Ensure.NotNull(repositoryPath);

		List<string> query = [];

		if (!string.IsNullOrEmpty(cursor))
		{
			query.Add($"cursor={Uri.EscapeDataString(cursor)}");
		}

		if (limit is int size)
		{
			query.Add($"limit={size.ToString(CultureInfo.InvariantCulture)}");
		}

		string path = string.IsNullOrEmpty(repositoryPath) ? "locks" : $"{repositoryPath}/locks";
		string suffix = query.Count == 0 ? string.Empty : $"?{string.Join('&', query)}";

		HttpRequestMessage request = new(HttpMethod.Get, Combine(upstreamBase, path) + suffix);

		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LfsMediaType));
		ApplyAuthorization(request, authorization);

		return request;
	}

	/// <summary>
	/// Builds the request that fetches an object from upstream on a cache miss.
	/// </summary>
	/// <param name="token">The token carrying the upstream action.</param>
	/// <param name="range">The client's Range header, forwarded when present.</param>
	/// <returns>The request to send upstream.</returns>
	public static HttpRequestMessage BuildObjectRequest(HrefToken token, string? range)
	{
		Ensure.NotNull(token);

		HttpRequestMessage request = new(HttpMethod.Get, token.UpstreamHref);
		ApplyTokenHeaders(request, token);

		// Parsed rather than added raw, so a malformed client range is dropped here instead of being
		// forwarded for upstream to reject.
		if (!string.IsNullOrEmpty(range) && RangeHeaderValue.TryParse(range, out RangeHeaderValue? parsed))
		{
			request.Headers.Range = parsed;
		}

		return request;
	}

	/// <summary>
	/// Builds the request that relays a client upload to upstream.
	/// </summary>
	/// <param name="token">The token carrying the upstream action.</param>
	/// <param name="body">The client's body, streamed through rather than buffered.</param>
	/// <param name="contentLength">
	/// The length the client declared, forwarded so upstream is not asked to accept a chunked upload
	/// it may reject. Null when the client did not declare one.
	/// </param>
	/// <returns>The request to send upstream.</returns>
	public static HttpRequestMessage BuildUploadRequest(HrefToken token, Stream body, long? contentLength)
	{
		Ensure.NotNull(token);
		Ensure.NotNull(body);

		HttpRequestMessage request = new(HttpMethod.Put, token.UpstreamHref)
		{
			Content = new StreamContent(body),
		};

		if (contentLength is not null)
		{
			request.Content.Headers.ContentLength = contentLength;
		}

		ApplyTokenHeaders(request, token);
		return request;
	}

	/// <summary>
	/// Builds the request that relays a verify call to upstream.
	/// </summary>
	/// <param name="token">The token carrying the upstream action.</param>
	/// <param name="body">The client's verify body.</param>
	/// <returns>The request to send upstream.</returns>
	public static HttpRequestMessage BuildVerifyRequest(HrefToken token, Stream body)
	{
		Ensure.NotNull(token);
		Ensure.NotNull(body);

		HttpRequestMessage request = new(HttpMethod.Post, token.UpstreamHref)
		{
			Content = new StreamContent(body),
		};

		request.Content.Headers.ContentType = new MediaTypeHeaderValue(LfsMediaType);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(LfsMediaType));
		ApplyTokenHeaders(request, token);

		return request;
	}

	/// <summary>
	/// Builds a verbatim relay of any other request under an upstream prefix.
	/// </summary>
	/// <param name="upstreamBase">The configured upstream base URL.</param>
	/// <param name="method">The client's method.</param>
	/// <param name="path">The path after the upstream key, with no leading slash.</param>
	/// <param name="query">The client's query string, including the leading question mark, or empty.</param>
	/// <param name="body">The client's body.</param>
	/// <param name="headers">The client's headers, filtered of hop-by-hop entries.</param>
	/// <returns>The request to send upstream.</returns>
	public static HttpRequestMessage BuildRelayRequest(
		Uri upstreamBase,
		string method,
		string path,
		string query,
		Stream body,
		IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
	{
		Ensure.NotNull(upstreamBase);
		Ensure.NotNull(headers);
		Ensure.NotNull(body);
		Ensure.NotNull(method);
		Ensure.NotNull(path);
		Ensure.NotNull(query);

		HttpRequestMessage request = new(new HttpMethod(method), Combine(upstreamBase, path) + query);

		if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
		{
			request.Content = new StreamContent(body);
		}

		foreach ((string name, IEnumerable<string> values) in headers)
		{
			if (HopHeaders.Contains(name))
			{
				continue;
			}

			// Content headers belong on the content, and adding them to the request headers throws.
			if (!request.Headers.TryAddWithoutValidation(name, values))
			{
				request.Content?.Headers.TryAddWithoutValidation(name, values);
			}
		}

		return request;
	}

	/// <summary>
	/// Reports whether a header describes the hop rather than the request, and so is not forwarded.
	/// </summary>
	/// <param name="name">The header name.</param>
	/// <returns><see langword="true"/> when the header must not be forwarded.</returns>
	public static bool IsHopHeader(string name) => HopHeaders.Contains(name);

	private static void ApplyTokenHeaders(HttpRequestMessage request, HrefToken token)
	{
		foreach ((string name, string value) in token.UpstreamHeaders)
		{
			if (HopHeaders.Contains(name))
			{
				continue;
			}

			if (!request.Headers.TryAddWithoutValidation(name, value))
			{
				request.Content?.Headers.TryAddWithoutValidation(name, value);
			}
		}
	}

	private static void ApplyAuthorization(HttpRequestMessage request, string? authorization)
	{
		if (!string.IsNullOrEmpty(authorization))
		{
			// Added without validation so an unusual but upstream-acceptable scheme is not rejected
			// here. The proxy is not the authority on what upstream accepts.
			request.Headers.TryAddWithoutValidation("Authorization", authorization);
		}
	}

	/// <summary>
	/// Joins a path onto an upstream base URL, preserving any path the base URL already carries.
	/// </summary>
	/// <remarks>
	/// <c>new Uri(baseUrl, path)</c> is wrong here: it discards the base URL's own path, so an
	/// upstream configured as <c>https://dev.azure.com/org</c> would lose the organization segment.
	/// </remarks>
	private static Uri Combine(Uri upstreamBase, string path) =>
		new($"{upstreamBase.ToString().TrimEnd('/')}/{path.TrimStart('/')}");
}
