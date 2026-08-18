// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Batch;

using System.Text.Json.Nodes;
using ktsu.Essentials.EncryptionProviders.Aes;
using ktsu.GitLfsCache.Batch;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class BatchRewriterTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	private static (BatchRewriter Rewriter, HrefTokenCodec Codec) Create()
	{
		GitLfsCacheOptions options = new() { TokenLifetime = TimeSpan.FromHours(1) };
		options.TokenKeys.Add(Convert.ToBase64String(new byte[32]));
		FakeTimeProvider time = new(Now);
		HrefTokenCodec codec = new(new AesEncryptionProvider(), Options.Create(options), time);
		return (new BatchRewriter(codec, Options.Create(options), time), codec);
	}

	private static BatchRewriteContext Context() => new()
	{
		Upstream = "github",
		RepositoryPath = "owner/repo.git/info/lfs",
		PublicBaseUrl = new Uri("https://cache.example"),
	};

	private static JsonNode Load(string fileName) =>
		JsonNode.Parse(File.ReadAllText(Path.Combine("TestData", fileName)))!;

	private static JsonObject FirstAction(JsonNode rewritten, string action) =>
		rewritten["objects"]![0]!["actions"]![action]!.AsObject();

	private static HrefToken Decode(HrefTokenCodec codec, JsonObject action)
	{
		string href = action["href"]!.GetValue<string>();
		string encoded = new Uri(href).Query.Replace("?t=", string.Empty, StringComparison.Ordinal);

		Assert.IsTrue(codec.TryDecode(encoded, out HrefToken? token, out string? failure), failure);
		Assert.IsNotNull(token);
		return token;
	}

	[TestMethod]
	public void Rewrite_DownloadHref_PointsAtTheProxy()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-download-batch.json"), Context());
		string href = FirstAction(rewritten, "download")["href"]!.GetValue<string>();

		Assert.StartsWith(
			"https://cache.example/github/owner/repo.git/info/lfs/objects/9a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8?t=",
			href);
	}

	[TestMethod]
	public void Rewrite_DownloadToken_CarriesTheUpstreamAction()
	{
		(BatchRewriter rewriter, HrefTokenCodec codec) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("ado-download-batch.json"), Context());
		HrefToken token = Decode(codec, FirstAction(rewritten, "download"));

		Assert.AreEqual("https://dev.azure.com/org/_apis/lfs/objects/1111", token.UpstreamHref);
		Assert.AreEqual("Bearer ado-secret-token", token.UpstreamHeaders["Authorization"]);
		Assert.AreEqual("Suppress", token.UpstreamHeaders["X-TFS-FedAuthRedirect"]);
		Assert.AreEqual(TokenAction.Download, token.Action);
		Assert.AreEqual("github", token.Upstream);
		Assert.AreEqual(500L, token.Size);
		Assert.AreEqual(Now.AddHours(1), token.ExpiresAt);
	}

	[TestMethod]
	public void Rewrite_RemovesTheUpstreamCredentialFromTheResponse()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("ado-download-batch.json"), Context());

		Assert.IsFalse(FirstAction(rewritten, "download").ContainsKey("header"));
		Assert.DoesNotContain("ado-secret-token", rewritten.ToJsonString());
	}

	[TestMethod]
	public void Rewrite_SetsExpiresInFromTokenLifetimeAndDropsExpiresAt()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonObject action = FirstAction(rewriter.Rewrite(Load("ado-download-batch.json"), Context()), "download");

		Assert.AreEqual(3600, action["expires_in"]!.GetValue<int>());
		Assert.IsFalse(action.ContainsKey("expires_at"));
	}

	[TestMethod]
	public void Rewrite_PreservesUnknownProperties()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-download-batch.json"), Context());

		Assert.AreEqual("sha256", rewritten["hash_algo"]!.GetValue<string>());
		Assert.AreEqual("basic", rewritten["transfer"]!.GetValue<string>());
		Assert.IsTrue(rewritten["objects"]![0]!["authenticated"]!.GetValue<bool>());
		Assert.AreEqual(12345L, rewritten["objects"]![0]!["size"]!.GetValue<long>());
	}

	[TestMethod]
	public void Rewrite_UploadAndVerify_BothPointAtTheProxyWithDistinctRoutes()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-upload-batch.json"), Context());
		string upload = FirstAction(rewritten, "upload")["href"]!.GetValue<string>();
		string verify = FirstAction(rewritten, "verify")["href"]!.GetValue<string>();

		const string oid = "2222222222222222222222222222222222222222222222222222222222222222";
		Assert.StartsWith($"https://cache.example/github/owner/repo.git/info/lfs/objects/{oid}?t=", upload);
		Assert.StartsWith($"https://cache.example/github/owner/repo.git/info/lfs/objects/{oid}/verify?t=", verify);
	}

	[TestMethod]
	public void Rewrite_UploadAndVerifyTokens_CarryDistinctActions()
	{
		(BatchRewriter rewriter, HrefTokenCodec codec) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-upload-batch.json"), Context());

		Assert.AreEqual(TokenAction.Upload, Decode(codec, FirstAction(rewritten, "upload")).Action);
		Assert.AreEqual(TokenAction.Verify, Decode(codec, FirstAction(rewritten, "verify")).Action);
	}

	[TestMethod]
	public void Rewrite_ObjectWithAnError_PassesThroughUntouched()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("mixed-error-batch.json"), Context());
		JsonNode failed = rewritten["objects"]![1]!;

		Assert.AreEqual(404, failed["error"]!["code"]!.GetValue<int>());
		Assert.AreEqual("Object does not exist", failed["error"]!["message"]!.GetValue<string>());
		Assert.IsFalse(failed.AsObject().ContainsKey("actions"));
	}

	[TestMethod]
	public void Rewrite_ObjectWithAnError_DoesNotStopTheOthers()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("mixed-error-batch.json"), Context());

		Assert.StartsWith("https://cache.example/", FirstAction(rewritten, "download")["href"]!.GetValue<string>());
	}

	[TestMethod]
	public void Rewrite_ResponseWithNoObjectsArray_IsReturnedUnchanged()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse("""{"message":"Repository not found","documentation_url":"x"}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.AreEqual("Repository not found", rewritten["message"]!.GetValue<string>());
	}

	[TestMethod]
	public void Rewrite_ObjectWithEmptyActions_IsLeftAlone()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse("""{"objects":[{"oid":"aa","size":1,"actions":{}}]}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.HasCount(0, rewritten["objects"]![0]!["actions"]!.AsObject());
	}

	[TestMethod]
	public void Rewrite_UnknownActionName_IsLeftUntouched()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse(
			"""{"objects":[{"oid":"aa","size":1,"actions":{"custom":{"href":"https://upstream.example/x"}}}]}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.AreEqual(
			"https://upstream.example/x",
			rewritten["objects"]![0]!["actions"]!["custom"]!["href"]!.GetValue<string>());
	}

	[TestMethod]
	public void Rewrite_ActionWithNoHref_IsLeftAlone()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse("""{"objects":[{"oid":"aa","size":1,"actions":{"download":{}}}]}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.HasCount(0, FirstAction(rewritten, "download"));
	}

	[TestMethod]
	public void Rewrite_DoesNotMutateTheInputNode()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = Load("ado-download-batch.json");
		string before = input.ToJsonString();

		rewriter.Rewrite(input, Context());

		Assert.AreEqual(before, input.ToJsonString());
	}

	[TestMethod]
	public void Rewrite_PublicBaseUrlWithTrailingSlash_DoesNotDoubleTheSeparator()
	{
		(BatchRewriter rewriter, _) = Create();
		BatchRewriteContext context = Context() with { PublicBaseUrl = new Uri("https://cache.example/") };

		JsonNode rewritten = rewriter.Rewrite(Load("github-download-batch.json"), context);

		Assert.StartsWith("https://cache.example/github/", FirstAction(rewritten, "download")["href"]!.GetValue<string>());
	}
}
