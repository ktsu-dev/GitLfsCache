// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tokens;

using System.Buffers.Text;
using ktsu.Essentials.EncryptionProviders.Aes;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class HrefTokenCodecTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	private static string Key(byte seed)
	{
		byte[] key = new byte[32];
		Array.Fill(key, seed);
		return Convert.ToBase64String(key);
	}

	private static (HrefTokenCodec Codec, FakeTimeProvider Time) Create(params string[] keys)
	{
		GitLfsCacheOptions options = new() { TokenLifetime = TimeSpan.FromHours(1) };

		foreach (string key in keys.Length == 0 ? [Key(1)] : keys)
		{
			options.TokenKeys.Add(key);
		}

		FakeTimeProvider time = new(Now);
		return (new HrefTokenCodec(new AesEncryptionProvider(), Options.Create(options), time), time);
	}

	private static HrefToken Token() => new()
	{
		Oid = "9a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8",
		Size = 1234567,
		Upstream = "github",
		Action = TokenAction.Download,
		UpstreamHref = "https://objects.githubusercontent.com/really/long/signed/url?sig=abc",
		UpstreamHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer upstream-secret" },
		ExpiresAt = Now.AddHours(1),
	};

	[TestMethod]
	public void EncodeThenDecode_RoundTripsEveryField()
	{
		(HrefTokenCodec codec, _) = Create();
		HrefToken original = Token();

		string encoded = codec.Encode(original);

		Assert.IsTrue(codec.TryDecode(encoded, out HrefToken? decoded, out string? failure), failure);
		Assert.IsNotNull(decoded);
		Assert.AreEqual(original.Oid, decoded.Oid);
		Assert.AreEqual(original.Size, decoded.Size);
		Assert.AreEqual(original.Upstream, decoded.Upstream);
		Assert.AreEqual(original.Action, decoded.Action);
		Assert.AreEqual(original.UpstreamHref, decoded.UpstreamHref);
		Assert.AreEqual(original.ExpiresAt, decoded.ExpiresAt);
		Assert.AreEqual("Bearer upstream-secret", decoded.UpstreamHeaders["Authorization"]);
	}

	[TestMethod]
	public void Encode_ProducesUrlSafeOutput()
	{
		(HrefTokenCodec codec, _) = Create();

		string encoded = codec.Encode(Token());

		Assert.DoesNotContain("+", encoded);
		Assert.DoesNotContain("/", encoded);
		Assert.DoesNotContain("=", encoded);
		Assert.AreEqual(Uri.EscapeDataString(encoded), encoded);
	}

	[TestMethod]
	public void Encode_SameTokenTwice_ProducesDifferentCiphertext()
	{
		(HrefTokenCodec codec, _) = Create();
		HrefToken token = Token();

		Assert.AreNotEqual(codec.Encode(token), codec.Encode(token));
	}

	[TestMethod]
	public void TryDecode_AfterExpiry_FailsNamingExpiry()
	{
		(HrefTokenCodec codec, FakeTimeProvider time) = Create();
		string encoded = codec.Encode(Token());

		time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

		Assert.IsFalse(codec.TryDecode(encoded, out HrefToken? decoded, out string? failure));
		Assert.IsNull(decoded);
		Assert.IsNotNull(failure);
		Assert.Contains("expired", failure, StringComparison.OrdinalIgnoreCase);
	}

	[TestMethod]
	public void TryDecode_TamperedByte_IsRejectedAtEveryOffset()
	{
		(HrefTokenCodec codec, _) = Create();
		byte[] raw = Base64Url.DecodeFromChars(codec.Encode(Token()));

		for (int offset = 0; offset < raw.Length; offset++)
		{
			byte[] tampered = [.. raw];
			tampered[offset] ^= 0x01;
			string encoded = Base64Url.EncodeToString(tampered);

			Assert.IsFalse(
				codec.TryDecode(encoded, out HrefToken? decoded, out string? _),
				$"A token tampered at byte {offset} was accepted.");
			Assert.IsNull(decoded);
		}
	}

	[TestMethod]
	public void TryDecode_TruncatedToken_IsRejected()
	{
		(HrefTokenCodec codec, _) = Create();
		string encoded = codec.Encode(Token());

		Assert.IsFalse(codec.TryDecode(encoded[..(encoded.Length / 2)], out HrefToken? _, out string? _));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("!!!not base64url!!!")]
	[DataRow("aaaa")]
	public void TryDecode_Garbage_IsRejectedWithoutThrowing(string encoded)
	{
		(HrefTokenCodec codec, _) = Create();

		Assert.IsFalse(codec.TryDecode(encoded, out HrefToken? _, out string? failure));
		Assert.IsNotNull(failure);
	}

	[TestMethod]
	public void TryDecode_Null_IsRejectedWithoutThrowing()
	{
		(HrefTokenCodec codec, _) = Create();

		Assert.IsFalse(codec.TryDecode(null, out HrefToken? _, out string? _));
	}

	[TestMethod]
	public void TryDecode_TokenFromRotatedOutKey_StillDecodes()
	{
		(HrefTokenCodec oldCodec, _) = Create(Key(2));
		string encoded = oldCodec.Encode(Token());

		// New key first, old key retained: encryption uses the new one, decryption tries both.
		(HrefTokenCodec rotated, _) = Create(Key(3), Key(2));

		Assert.IsTrue(rotated.TryDecode(encoded, out HrefToken? decoded, out string? failure), failure);
		Assert.IsNotNull(decoded);
	}

	[TestMethod]
	public void Encode_AfterRotation_UsesTheFirstKey()
	{
		(HrefTokenCodec rotated, _) = Create(Key(3), Key(2));
		string encoded = rotated.Encode(Token());

		// A codec holding only the new key must still accept it, proving the new key encrypted it.
		(HrefTokenCodec newOnly, _) = Create(Key(3));

		Assert.IsTrue(newOnly.TryDecode(encoded, out HrefToken? _, out string? failure), failure);
	}

	[TestMethod]
	public void TryDecode_TokenFromAnUnknownKey_IsRejected()
	{
		(HrefTokenCodec foreign, _) = Create(Key(9));
		string encoded = foreign.Encode(Token());

		(HrefTokenCodec local, _) = Create(Key(1));

		Assert.IsFalse(local.TryDecode(encoded, out HrefToken? _, out string? failure));
		Assert.IsNotNull(failure);
	}

	[TestMethod]
	public void EncodeThenDecode_WithEmptyHeaders_RoundTrips()
	{
		(HrefTokenCodec codec, _) = Create();
		HrefToken token = Token() with { UpstreamHeaders = new Dictionary<string, string>() };

		Assert.IsTrue(codec.TryDecode(codec.Encode(token), out HrefToken? decoded, out string? _));
		Assert.IsNotNull(decoded);
		Assert.HasCount(0, decoded.UpstreamHeaders);
	}

	[TestMethod]
	public void Encode_DoesNotLeakThePayloadInPlainText()
	{
		(HrefTokenCodec codec, _) = Create();

		string encoded = codec.Encode(Token());

		Assert.DoesNotContain("upstream-secret", encoded);
		Assert.DoesNotContain("githubusercontent", encoded);
	}
}
