// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Upstreams;

using ktsu.GitLfsCache.Tokens;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class UpstreamRequestsTests
{
	private static HrefToken Token(string action = TokenAction.Download) => new()
	{
		Oid = new string('a', 64),
		Size = 100,
		Upstream = "github",
		Action = action,
		UpstreamHref = "https://objects.example/storage/abc?sig=xyz",
		UpstreamHeaders = new Dictionary<string, string>
		{
			["Authorization"] = "Bearer upstream-secret",
			["X-Custom"] = "kept",
			["Host"] = "should-be-dropped",
		},
		ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
	};

	private static MemoryStream Body(string content = "{}") =>
		new(System.Text.Encoding.UTF8.GetBytes(content));

	[TestMethod]
	public void BuildBatchRequest_TargetsTheUpstreamBatchEndpoint()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			new Uri("https://github.com"),
			"owner/repo.git/info/lfs",
			Body(),
			null);

		Assert.AreEqual(HttpMethod.Post, request.Method);
		Assert.AreEqual(
			new Uri("https://github.com/owner/repo.git/info/lfs/objects/batch"),
			request.RequestUri);
	}

	[TestMethod]
	public void BuildBatchRequest_PreservesAPathOnTheUpstreamBaseUrl()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			new Uri("https://dev.azure.com/org"),
			"project/_git/repo/info/lfs",
			Body(),
			null);

		// new Uri(base, path) would drop the /org segment, which is the bug this asserts against.
		Assert.AreEqual(
			new Uri("https://dev.azure.com/org/project/_git/repo/info/lfs/objects/batch"),
			request.RequestUri);
	}

	[TestMethod]
	public void BuildBatchRequest_ForwardsTheClientAuthorizationUnchanged()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			new Uri("https://github.com"),
			"owner/repo.git/info/lfs",
			Body(),
			"Basic dXNlcjp0b2tlbg==");

		Assert.AreEqual("Basic dXNlcjp0b2tlbg==", request.Headers.GetValues("Authorization").Single());
	}

	[TestMethod]
	public void BuildBatchRequest_NoAuthorization_SendsNone()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			new Uri("https://github.com"),
			"owner/repo.git/info/lfs",
			Body(),
			null);

		Assert.IsFalse(request.Headers.Contains("Authorization"));
	}

	[TestMethod]
	public void BuildBatchRequest_UsesTheLfsMediaType()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildBatchRequest(
			new Uri("https://github.com"),
			"owner/repo.git/info/lfs",
			Body(),
			null);

		Assert.AreEqual(UpstreamRequests.LfsMediaType, request.Content!.Headers.ContentType!.MediaType);
		Assert.AreEqual(UpstreamRequests.LfsMediaType, request.Headers.Accept.Single().MediaType);
	}

	[TestMethod]
	public void BuildObjectRequest_TargetsTheHrefFromTheToken()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildObjectRequest(Token(), null);

		Assert.AreEqual(HttpMethod.Get, request.Method);
		Assert.AreEqual(new Uri("https://objects.example/storage/abc?sig=xyz"), request.RequestUri);
	}

	[TestMethod]
	public void BuildObjectRequest_AppliesTheTokenHeadersButNotHopHeaders()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildObjectRequest(Token(), null);

		Assert.AreEqual("Bearer upstream-secret", request.Headers.GetValues("Authorization").Single());
		Assert.AreEqual("kept", request.Headers.GetValues("X-Custom").Single());
		Assert.IsFalse(request.Headers.Contains("Host"));
	}

	[TestMethod]
	public void BuildObjectRequest_WithRange_ForwardsIt()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildObjectRequest(Token(), "bytes=100-199");

		Assert.IsNotNull(request.Headers.Range);
		Assert.AreEqual(100L, request.Headers.Range.Ranges.Single().From);
		Assert.AreEqual(199L, request.Headers.Range.Ranges.Single().To);
	}

	[TestMethod]
	public void BuildObjectRequest_WithMalformedRange_SendsNoRange()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildObjectRequest(Token(), "not-a-range");

		Assert.IsNull(request.Headers.Range);
	}

	[TestMethod]
	public void BuildUploadRequest_IsAPutCarryingTheDeclaredLength()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildUploadRequest(
			Token(TokenAction.Upload),
			Body("payload"),
			contentLength: 7);

		Assert.AreEqual(HttpMethod.Put, request.Method);
		Assert.AreEqual(7L, request.Content!.Headers.ContentLength);
		Assert.AreEqual("Bearer upstream-secret", request.Headers.GetValues("Authorization").Single());
	}

	[TestMethod]
	public void BuildUploadRequest_NoDeclaredLength_LeavesItUnset()
	{
		// A non-seekable body, which is what an incoming request actually is. StreamContent infers a
		// length from a seekable stream, so a MemoryStream here would not exercise the real case.
		using NonSeekableStream body = new(Body("payload"));

		using HttpRequestMessage request = UpstreamRequests.BuildUploadRequest(
			Token(TokenAction.Upload),
			body,
			contentLength: null);

		Assert.IsNull(request.Content!.Headers.ContentLength);
	}

	/// <summary>A read-only stream that hides its length, standing in for an incoming request body.</summary>
	private sealed class NonSeekableStream(Stream inner) : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush() => inner.Flush();

		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	[TestMethod]
	public void BuildVerifyRequest_IsAPostWithTheLfsMediaType()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildVerifyRequest(
			Token(TokenAction.Verify),
			Body());

		Assert.AreEqual(HttpMethod.Post, request.Method);
		Assert.AreEqual(UpstreamRequests.LfsMediaType, request.Content!.Headers.ContentType!.MediaType);
	}

	[TestMethod]
	public void BuildRelayRequest_JoinsThePathAndQuery()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildRelayRequest(
			new Uri("https://github.com"),
			"GET",
			"owner/repo.git/info/lfs/locks",
			"?refspec=main",
			Body(),
			[]);

		Assert.AreEqual(
			new Uri("https://github.com/owner/repo.git/info/lfs/locks?refspec=main"),
			request.RequestUri);
	}

	[TestMethod]
	public void BuildRelayRequest_GetHasNoBody()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildRelayRequest(
			new Uri("https://github.com"),
			"GET",
			"path",
			string.Empty,
			Body(),
			[]);

		Assert.IsNull(request.Content);
	}

	[TestMethod]
	public void BuildRelayRequest_PostCarriesTheBody()
	{
		using HttpRequestMessage request = UpstreamRequests.BuildRelayRequest(
			new Uri("https://github.com"),
			"POST",
			"path",
			string.Empty,
			Body("{}"),
			[]);

		Assert.IsNotNull(request.Content);
	}

	[TestMethod]
	public void BuildRelayRequest_ForwardsClientHeadersButNotHopHeaders()
	{
		KeyValuePair<string, IEnumerable<string>>[] headers =
		[
			new("Authorization", ["Basic abc"]),
			new("Accept", ["application/vnd.git-lfs+json"]),
			new("Host", ["cache.example"]),
			new("Connection", ["keep-alive"]),
			new("Transfer-Encoding", ["chunked"]),
		];

		using HttpRequestMessage request = UpstreamRequests.BuildRelayRequest(
			new Uri("https://github.com"),
			"POST",
			"path",
			string.Empty,
			Body(),
			headers);

		Assert.AreEqual("Basic abc", request.Headers.GetValues("Authorization").Single());
		Assert.IsTrue(request.Headers.Contains("Accept"));
		Assert.IsFalse(request.Headers.Contains("Host"));
		Assert.IsFalse(request.Headers.Contains("Connection"));
		Assert.IsFalse(request.Headers.Contains("Transfer-Encoding"));
	}

	[TestMethod]
	[DataRow("Host")]
	[DataRow("host")]
	[DataRow("Content-Length")]
	[DataRow("Transfer-Encoding")]
	[DataRow("Connection")]
	public void IsHopHeader_RecognizesPerConnectionHeadersCaseInsensitively(string name)
	{
		Assert.IsTrue(UpstreamRequests.IsHopHeader(name));
	}

	[TestMethod]
	[DataRow("Authorization")]
	[DataRow("Accept")]
	[DataRow("X-Custom")]
	public void IsHopHeader_LeavesRequestHeadersAlone(string name)
	{
		Assert.IsFalse(UpstreamRequests.IsHopHeader(name));
	}
}
