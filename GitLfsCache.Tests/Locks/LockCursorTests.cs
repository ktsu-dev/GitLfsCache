// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using ktsu.GitLfsCache.Locks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LockCursorTests
{
	private static readonly Guid SnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555");

	[TestMethod]
	public void Encode_ThenDecode_RoundTrips()
	{
		LockCursor original = new(SnapshotId, 250);

		Assert.IsTrue(LockCursor.TryDecode(original.Encode(), out LockCursor? decoded));
		Assert.AreEqual(original, decoded);
	}

	[TestMethod]
	public void Encode_IsUrlSafe()
	{
		// It travels as a query parameter, so anything needing escaping there is a bug waiting for a
		// client that does not escape it.
		string encoded = new LockCursor(SnapshotId, 250).Encode();

		Assert.DoesNotContain("+", encoded);
		Assert.DoesNotContain("/", encoded);
		Assert.DoesNotContain("=", encoded);
	}

	[TestMethod]
	public void Encode_DifferentSnapshots_ProduceDifferentCursors()
	{
		string first = new LockCursor(Guid.NewGuid(), 0).Encode();
		string second = new LockCursor(Guid.NewGuid(), 0).Encode();

		Assert.AreNotEqual(first, second);
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	[DataRow("not-base64-!!")]
	[DataRow("YWJj")]
	public void TryDecode_Malformed_IsRefusedRatherThanThrowing(string? encoded)
	{
		// Cursors arrive from the network. A client sending nonsense should get a fresh first page,
		// never a 500.
		Assert.IsFalse(LockCursor.TryDecode(encoded, out LockCursor? cursor));
		Assert.IsNull(cursor);
	}

	[TestMethod]
	[DataRow("nonsense:0")]
	[DataRow("111111112222333344445555555555555")]
	public void TryDecode_WellFormedBase64WithBadContent_IsRefused(string plain)
	{
		string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');

		Assert.IsFalse(LockCursor.TryDecode(encoded, out _));
	}

	[TestMethod]
	public void TryDecode_NegativeOffset_IsRefused()
	{
		// A negative offset would be clamped rather than rejected downstream, so a client could ask for
		// a page it never got a cursor for. Refusing keeps the cursor meaning exactly one position.
		string encoded = Convert.ToBase64String(
			System.Text.Encoding.UTF8.GetBytes($"{SnapshotId:N}:-1"))
			.TrimEnd('=');

		Assert.IsFalse(LockCursor.TryDecode(encoded, out _));
	}
}
