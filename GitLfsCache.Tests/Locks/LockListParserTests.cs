// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockListParserTests
{
	private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

	[TestMethod]
	public void TryParsePage_APage_ReadsEveryLock()
	{
		JsonNode body = Parse(
			"""
			{"locks":[
			  {"id":"871","path":"Content/a.uasset","locked_at":"2026-08-19T09:47:00Z","owner":{"name":"someone"}},
			  {"id":"872","path":"Content/b.uasset","locked_at":"2026-08-19T09:48:00Z","owner":{"name":"someone else"}}
			]}
			""");

		Assert.IsTrue(LockListParser.TryParsePage(body, out IReadOnlyList<LockEntry>? entries, out string? cursor));
		Assert.HasCount(2, entries);
		Assert.AreEqual("871", entries[0].Id);
		Assert.AreEqual("Content/b.uasset", entries[1].Path);
		Assert.IsNull(cursor);
	}

	[TestMethod]
	public void TryParsePage_KeepsFieldsTheProxyDoesNotModel()
	{
		// The locking API is documented as a first version designed to be extended, so a field the
		// proxy has never heard of must still reach the client.
		JsonNode body = Parse(
			"""{"locks":[{"id":"871","path":"a.uasset","owner":{"name":"someone"},"some_future_field":42}]}""");

		Assert.IsTrue(LockListParser.TryParsePage(body, out IReadOnlyList<LockEntry>? entries, out _));
		Assert.AreEqual(42, entries[0].Payload["some_future_field"]!.GetValue<int>());
		Assert.AreEqual("someone", entries[0].Payload["owner"]!["name"]!.GetValue<string>());
	}

	[TestMethod]
	public void TryParsePage_NextCursor_IsRead()
	{
		JsonNode body = Parse("""{"locks":[{"id":"1","path":"a"}],"next_cursor":"page-2"}""");

		Assert.IsTrue(LockListParser.TryParsePage(body, out _, out string? cursor));
		Assert.AreEqual("page-2", cursor);
	}

	[TestMethod]
	[DataRow("""{"locks":[],"next_cursor":""}""")]
	[DataRow("""{"locks":[]}""")]
	public void TryParsePage_NoFurtherPages_ReportsNoCursor(string json)
	{
		// An empty string is not a cursor. Treating it as one is an infinite walk.
		Assert.IsTrue(LockListParser.TryParsePage(Parse(json), out _, out string? cursor));
		Assert.IsNull(cursor);
	}

	[TestMethod]
	public void TryParsePage_OmittedLocksArray_IsAnEmptyPage()
	{
		// Forges differ on whether they send an empty array or omit it, and refusing one spelling
		// would drop the cache to relaying for any repository that simply has no locks.
		Assert.IsTrue(LockListParser.TryParsePage(Parse("{}"), out IReadOnlyList<LockEntry>? entries, out _));
		Assert.IsEmpty(entries);
	}

	[TestMethod]
	[DataRow("""{"locks":[{"path":"a.uasset"}]}""")]
	[DataRow("""{"locks":[{"id":"871"}]}""")]
	[DataRow("""{"locks":[{"id":"","path":"a.uasset"}]}""")]
	[DataRow("""{"locks":["not an object"]}""")]
	[DataRow("""{"locks":[{"id":42,"path":"a.uasset"}]}""")]
	[DataRow("""{"locks":[{"id":"1","path":["a"]}]}""")]
	[DataRow("[]")]
	public void TryParsePage_Malformed_IsRefusedRatherThanPartiallyRead(string json)
	{
		// A lock quietly dropped from a listing reads to a client as a file nobody holds, which is the
		// one wrong answer this cache must never produce.
		Assert.IsFalse(LockListParser.TryParsePage(Parse(json), out IReadOnlyList<LockEntry>? entries, out _));
		Assert.IsNull(entries);
	}

	[TestMethod]
	public void TryParsePage_NonStringCursor_IsIgnoredRatherThanThrowing()
	{
		// GetValue<string> throws on a node of another kind, so an upstream sending a number here would
		// otherwise become an unhandled exception rather than a page with no continuation.
		Assert.IsTrue(LockListParser.TryParsePage(
			Parse("""{"locks":[],"next_cursor":42}"""),
			out _,
			out string? cursor));

		Assert.IsNull(cursor);
	}

	[TestMethod]
	public void BuildResponse_LastPage_OmitsTheCursor()
	{
		JsonObject body = LockListParser.BuildResponse([], nextCursor: null);

		Assert.IsFalse(body.ContainsKey("next_cursor"));
		Assert.IsEmpty(body["locks"]!.AsArray());
	}

	[TestMethod]
	public void BuildResponse_RoundTripsAPage()
	{
		JsonNode original = Parse(
			"""{"locks":[{"id":"871","path":"a.uasset","owner":{"name":"someone"}}],"next_cursor":"page-2"}""");

		Assert.IsTrue(LockListParser.TryParsePage(original, out IReadOnlyList<LockEntry>? entries, out string? cursor));

		JsonObject rebuilt = LockListParser.BuildResponse(entries, cursor);

		Assert.AreEqual(original.ToJsonString(), rebuilt.ToJsonString());
	}

	[TestMethod]
	public void BuildResponse_TheSameEntryTwice_DoesNotThrow()
	{
		// A JsonNode has a parent and cannot be attached twice, so entries have to be cloned on the
		// way out. One snapshot serves many concurrent responses.
		Assert.IsTrue(LockListParser.TryParsePage(
			Parse("""{"locks":[{"id":"1","path":"a"}]}"""),
			out IReadOnlyList<LockEntry>? entries,
			out _));

		JsonObject first = LockListParser.BuildResponse(entries, null);
		JsonObject second = LockListParser.BuildResponse(entries, null);

		Assert.AreEqual(first.ToJsonString(), second.ToJsonString());
	}
}
