// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Integration;

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// A stand-in Git LFS server that records what it was asked and answers from a script.
/// </summary>
/// <remarks>
/// Implemented as an <see cref="HttpMessageHandler"/> rather than a second web host, so a test can
/// assert on the exact request the proxy sent, including whether the client's Authorization header was
/// forwarded, without going near a socket.
/// </remarks>
internal sealed class StubUpstream : HttpMessageHandler
{
	private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
	private readonly List<RecordedRequest> _requests = [];
	private readonly Lock _gate = new();

	/// <summary>Gets the requests the proxy sent, in order.</summary>
	public IReadOnlyList<RecordedRequest> Requests
	{
		get
		{
			lock (_gate)
			{
				return [.. _requests];
			}
		}
	}

	/// <summary>Gets or sets the status the batch endpoint answers with.</summary>
	public HttpStatusCode BatchStatus { get; set; } = HttpStatusCode.OK;

	/// <summary>Gets or sets the body the batch endpoint answers with when it is not successful.</summary>
	public string BatchFailureBody { get; set; } = """{"message":"Repository not found"}""";

	/// <summary>Gets or sets the status an object fetch answers with.</summary>
	public HttpStatusCode ObjectStatus { get; set; } = HttpStatusCode.OK;

	/// <summary>Gets or sets the status an upload answers with.</summary>
	public HttpStatusCode UploadStatus { get; set; } = HttpStatusCode.OK;

	/// <summary>Gets or sets a value indicating whether uploads get a verify action.</summary>
	public bool IncludeVerifyAction { get; set; }

	/// <summary>
	/// Gets or sets content returned for an object fetch regardless of its object id, used to make
	/// upstream hand back bytes that do not match what was asked for.
	/// </summary>
	public byte[]? CorruptedContent { get; set; }

	/// <summary>Gets the bytes uploads delivered, keyed by object id.</summary>
	public Dictionary<string, byte[]> Uploaded { get; } = new(StringComparer.Ordinal);

	/// <summary>Registers an object this upstream can serve.</summary>
	/// <param name="oid">The object id.</param>
	/// <param name="content">The bytes to serve.</param>
	public void AddObject(string oid, byte[] content)
	{
		lock (_gate)
		{
			_objects[oid] = content;
		}
	}

	/// <summary>Gets how many times an object's bytes were fetched.</summary>
	/// <param name="oid">The object id.</param>
	/// <returns>The number of fetches.</returns>
	public int FetchCount(string oid) =>
		Requests.Count(request => request.Path.Contains($"/storage/{oid}", StringComparison.Ordinal));

	/// <inheritdoc />
	protected override async Task<HttpResponseMessage> SendAsync(
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		string path = request.RequestUri!.AbsolutePath;
		string? body = request.Content is null
			? null
			: await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

		lock (_gate)
		{
			_requests.Add(new RecordedRequest(
				request.Method.Method,
				path,
				request.Headers.TryGetValues("Authorization", out IEnumerable<string>? authorization)
					? string.Join(",", authorization)
					: null,
				request.Headers.Range?.ToString()));
		}

		if (path.EndsWith("/objects/batch", StringComparison.Ordinal))
		{
			return BuildBatchResponse(body);
		}

		if (path.Contains("/storage/", StringComparison.Ordinal))
		{
			return BuildObjectResponse(path);
		}

		if (path.Contains("/upload/", StringComparison.Ordinal))
		{
			return BuildUploadResponse(path, request, cancellationToken);
		}

		// Matched exactly rather than by suffix: the locks API has its own /locks/verify endpoint, and
		// treating that as an upload verification would hide the fact that it should be relayed.
		if (path == "/repo/verify")
		{
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
		}

		// Anything else stands in for a relayed endpoint such as the locks API.
		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent($"relayed {path}"),
		};
	}

	private HttpResponseMessage BuildBatchResponse(string? requestBody)
	{
		if (BatchStatus != HttpStatusCode.OK)
		{
			return new HttpResponseMessage(BatchStatus)
			{
				Content = new StringContent(BatchFailureBody, Encoding.UTF8, "application/json"),
			};
		}

		JsonNode parsed = JsonNode.Parse(requestBody ?? "{}")!;
		string operation = parsed["operation"]?.GetValue<string>() ?? "download";
		JsonArray objects = [];

		foreach (JsonNode? requested in parsed["objects"]?.AsArray() ?? [])
		{
			string oid = requested!["oid"]!.GetValue<string>();
			long size = requested["size"]!.GetValue<long>();

			if (operation == "upload")
			{
				JsonObject actions = new()
				{
					["upload"] = new JsonObject
					{
						["href"] = $"https://upstream.example/upload/{oid}",
						["header"] = new JsonObject { ["Authorization"] = "Bearer upstream-upload-secret" },
					},
				};

				if (IncludeVerifyAction)
				{
					actions["verify"] = new JsonObject
					{
						["href"] = "https://upstream.example/repo/verify",
						["header"] = new JsonObject { ["Authorization"] = "Bearer upstream-verify-secret" },
					};
				}

				objects.Add(new JsonObject { ["oid"] = oid, ["size"] = size, ["actions"] = actions });
				continue;
			}

			bool known;

			lock (_gate)
			{
				known = _objects.ContainsKey(oid);
			}

			if (!known)
			{
				objects.Add(new JsonObject
				{
					["oid"] = oid,
					["size"] = size,
					["error"] = new JsonObject { ["code"] = 404, ["message"] = "Object does not exist" },
				});
				continue;
			}

			objects.Add(new JsonObject
			{
				["oid"] = oid,
				["size"] = size,
				["authenticated"] = true,
				["actions"] = new JsonObject
				{
					["download"] = new JsonObject
					{
						["href"] = $"https://upstream.example/storage/{oid}",
						["header"] = new JsonObject { ["Authorization"] = "Bearer upstream-download-secret" },
					},
				},
			});
		}

		JsonObject response = new()
		{
			["transfer"] = "basic",
			["objects"] = objects,
			["hash_algo"] = "sha256",
		};

		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(
				response.ToJsonString(),
				Encoding.UTF8,
				"application/vnd.git-lfs+json"),
		};
	}

	private HttpResponseMessage BuildObjectResponse(string path)
	{
		if (ObjectStatus != HttpStatusCode.OK)
		{
			return new HttpResponseMessage(ObjectStatus) { Content = new StringContent("denied") };
		}

		string oid = path[(path.LastIndexOf('/') + 1)..];
		byte[] content;

		if (CorruptedContent is byte[] corrupted)
		{
			content = corrupted;
		}
		else
		{
			lock (_gate)
			{
				if (!_objects.TryGetValue(oid, out byte[]? stored))
				{
					return new HttpResponseMessage(HttpStatusCode.NotFound);
				}

				content = stored;
			}
		}

		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new ByteArrayContent(content),
		};
	}

	private HttpResponseMessage BuildUploadResponse(
		string path,
		HttpRequestMessage request,
		CancellationToken cancellationToken)
	{
		string oid = path[(path.LastIndexOf('/') + 1)..];

		if (request.Content is not null)
		{
			byte[] delivered = request.Content
				.ReadAsByteArrayAsync(cancellationToken)
				.GetAwaiter()
				.GetResult();

			lock (_gate)
			{
				Uploaded[oid] = delivered;
			}
		}

		return new HttpResponseMessage(UploadStatus);
	}

	/// <summary>One request the proxy sent upstream.</summary>
	/// <param name="Method">The HTTP method.</param>
	/// <param name="Path">The absolute path.</param>
	/// <param name="Authorization">The Authorization header, or null when absent.</param>
	/// <param name="Range">The Range header, or null when absent.</param>
	internal sealed record RecordedRequest(string Method, string Path, string? Authorization, string? Range);
}
