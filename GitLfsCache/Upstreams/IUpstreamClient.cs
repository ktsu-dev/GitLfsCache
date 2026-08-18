// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

/// <summary>
/// Sends requests to an upstream Git LFS server.
/// </summary>
public interface IUpstreamClient
{
	/// <summary>
	/// Sends a request upstream, returning as soon as the response headers arrive.
	/// </summary>
	/// <param name="request">The request to send. Ownership passes to this call.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The upstream response, with its body not yet read.</returns>
	public Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken);
}
