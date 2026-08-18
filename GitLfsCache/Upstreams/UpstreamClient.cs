// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

/// <summary>
/// Sends requests upstream over a configured <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// Every send uses <see cref="HttpCompletionOption.ResponseHeadersRead"/>. That is the whole reason
/// this wrapper exists: the default buffers the entire response body before returning, which would
/// load a multi-gigabyte object into memory and defeat the streaming design.
/// </remarks>
/// <param name="httpClient">The client to send on, configured by the host.</param>
public sealed class UpstreamClient(HttpClient httpClient) : IUpstreamClient
{
	/// <summary>The name the host registers this client's <see cref="HttpClient"/> under.</summary>
	public const string HttpClientName = "gitlfscache-upstream";

	/// <inheritdoc />
	public Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken) =>
		httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
}
