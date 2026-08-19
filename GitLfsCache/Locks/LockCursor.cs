// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

/// <summary>
/// A position within one snapshot, handed to a client as an opaque listing cursor.
/// </summary>
/// <remarks>
/// A cursor is only meaningful alongside the snapshot that produced it, so it carries that snapshot's
/// identity as well as the offset. Presented against a snapshot that has since been replaced it is
/// refused, and the caller restarts the walk: continuing at the same offset in a different snapshot
/// would silently skip or repeat locks, which is worse than an extra round trip.
/// <para>
/// Not encrypted or authenticated, unlike a transfer token, because it grants nothing. The worst a
/// forged cursor achieves is a page of a listing the caller was already admitted to read, starting
/// somewhere they chose. Base64url is applied so it is opaque enough that nobody builds a client that
/// depends on its shape.
/// </para>
/// </remarks>
/// <param name="SnapshotId">Identifies the snapshot this position belongs to.</param>
/// <param name="Offset">How many entries of the filtered result precede this page.</param>
public sealed record LockCursor(Guid SnapshotId, int Offset)
{
	private const char Separator = ':';

	/// <summary>
	/// Encodes this cursor for a listing response.
	/// </summary>
	/// <returns>The cursor as a client should see it.</returns>
	public string Encode()
	{
		string plain = string.Create(
			CultureInfo.InvariantCulture,
			$"{SnapshotId:N}{Separator}{Offset}");

		return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(plain));
	}

	/// <summary>
	/// Reads a cursor a client presented.
	/// </summary>
	/// <remarks>
	/// Every malformed shape returns false rather than throwing. A cursor arrives from the network and
	/// a client that sends nonsense should get a fresh first page, not a 500.
	/// </remarks>
	/// <param name="encoded">The cursor as the client sent it.</param>
	/// <param name="cursor">The decoded cursor.</param>
	/// <returns><see langword="true"/> when the cursor was well formed.</returns>
	public static bool TryDecode(string? encoded, [NotNullWhen(true)] out LockCursor? cursor)
	{
		cursor = null;

		if (string.IsNullOrEmpty(encoded))
		{
			return false;
		}

		byte[] decoded;

		try
		{
			decoded = Base64Url.DecodeFromChars(encoded);
		}
		catch (FormatException)
		{
			return false;
		}

		string plain = Encoding.UTF8.GetString(decoded);
		int separator = plain.IndexOf(Separator, StringComparison.Ordinal);

		if (separator <= 0)
		{
			return false;
		}

		if (!Guid.TryParseExact(plain[..separator], "N", out Guid snapshotId))
		{
			return false;
		}

		if (!int.TryParse(
			plain[(separator + 1)..],
			NumberStyles.None,
			CultureInfo.InvariantCulture,
			out int offset))
		{
			return false;
		}

		cursor = new LockCursor(snapshotId, offset);
		return true;
	}
}
