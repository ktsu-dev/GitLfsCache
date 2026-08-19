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

		if (path.EndsWith("/unlock", StringComparison.Ordinal))
		{
			return BuildLockChangeResponse(path);
		}

		if (path.EndsWith("/locks", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
		{
			return BuildLockChangeResponse(path);
		}

		if (path.EndsWith("/locks", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
		{
			return BuildLocksResponse(request.RequestUri!.Query);
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

	/// <summary>Gets or sets the status an individual lock change answers with.</summary>
	public HttpStatusCode LockChangeStatus { get; set; } = HttpStatusCode.Created;

	/// <summary>
	/// Gets or sets how many lock changes are throttled before one is allowed through, standing in for
	/// a forge's secondary rate limit.
	/// </summary>
	public int ThrottleLockChanges { get; set; }

	/// <summary>Gets or sets the Retry-After a throttled lock change reports, in seconds.</summary>
	public int ThrottleRetryAfterSeconds { get; set; } = 1;

	/// <summary>Gets how many lock changes were attempted, throttled ones included.</summary>
	public int LockChangeRequests => Requests.Count(request =>
		request.Path.EndsWith("/unlock", StringComparison.Ordinal)
		|| (request.Path.EndsWith("/locks", StringComparison.Ordinal) && request.Method == "POST"));

	/// <summary>
	/// Answers one lock creation or release, optionally throttling first.
	/// </summary>
	private HttpResponseMessage BuildLockChangeResponse(string path)
	{
		lock (_gate)
		{
			if (ThrottleLockChanges > 0)
			{
				ThrottleLockChanges--;

				HttpResponseMessage throttled = new(HttpStatusCode.TooManyRequests)
				{
					Content = new StringContent("""{"message":"slow down"}""", Encoding.UTF8, "application/json"),
				};

				throttled.Headers.TryAddWithoutValidation(
					"Retry-After",
					ThrottleRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

				return throttled;
			}
		}

		if (LockChangeStatus is not (HttpStatusCode.OK or HttpStatusCode.Created))
		{
			return new HttpResponseMessage(LockChangeStatus)
			{
				Content = new StringContent("""{"message":"already locked"}""", Encoding.UTF8, "application/json"),
			};
		}

		JsonObject body = new()
		{
			["lock"] = new JsonObject
			{
				["id"] = "new-lock",
				["path"] = path,
				["owner"] = new JsonObject { ["name"] = "someone" },
			},
		};

		return new HttpResponseMessage(LockChangeStatus)
		{
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
		};
	}

	/// <summary>Gets or sets the status the lock listing answers with.</summary>
	public HttpStatusCode LocksStatus { get; set; } = HttpStatusCode.OK;

	/// <summary>Gets the lock paths this upstream reports, in order.</summary>
	public List<string> Locks { get; } = [];

	/// <summary>Gets or sets how many locks the listing returns per page.</summary>
	public int LocksPageSize { get; set; } = 100;

	/// <summary>Gets how many lock listing pages were requested.</summary>
	public int LockPageRequests => Requests.Count(request =>
		request.Path.EndsWith("/locks", StringComparison.Ordinal));

	/// <summary>
	/// Answers a lock listing page, paginating exactly as a forge does so a test can prove the proxy
	/// walks every cursor rather than stopping at the first page.
	/// </summary>
	private HttpResponseMessage BuildLocksResponse(string query)
	{
		if (LocksStatus != HttpStatusCode.OK)
		{
			return new HttpResponseMessage(LocksStatus)
			{
				Content = new StringContent("""{"message":"no"}""", Encoding.UTF8, "application/json"),
			};
		}

		Dictionary<string, string> parsed = query.TrimStart('?')
			.Split('&', StringSplitOptions.RemoveEmptyEntries)
			.Select(pair => pair.Split('=', 2))
			.Where(pair => pair.Length == 2)
			.ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

		int offset = parsed.TryGetValue("cursor", out string? cursor) && int.TryParse(cursor, out int parsedOffset)
			? parsedOffset
			: 0;

		int pageSize = parsed.TryGetValue("limit", out string? requested) && int.TryParse(requested, out int limit)
			? limit
			: LocksPageSize;

		string[] page;

		lock (_gate)
		{
			page = [.. Locks.Skip(offset).Take(pageSize)];
		}

		JsonArray locks = [];

		for (int index = 0; index < page.Length; index++)
		{
			locks.Add(new JsonObject
			{
				["id"] = (offset + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
				["path"] = page[index],
				["owner"] = new JsonObject { ["name"] = "someone" },
			});
		}

		JsonObject body = new() { ["locks"] = locks };
		int next = offset + page.Length;

		if (next < Locks.Count)
		{
			body["next_cursor"] = next.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		return new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
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
