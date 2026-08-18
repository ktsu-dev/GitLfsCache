// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ktsu.Essentials;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Encrypt-then-authenticate token codec over Essentials' AES provider.
/// </summary>
/// <remarks>
/// Essentials' AES provider uses <c>Aes.Create()</c> defaults, which is CBC with PKCS7 padding and
/// no authentication. A token that is only encrypted is malleable, and this one carries an upstream
/// credential plus the object id that selects which bytes get served, so a message authentication
/// code is not optional. <see cref="HMACSHA256"/> and <see cref="HKDF"/> come from the base class
/// library because every Essentials hash provider is unkeyed; a keyed-hash provider is a recorded
/// gap to contribute back to Essentials.
/// <para>
/// Wire format, concatenated then Base64url encoded:
/// version (1 byte), key id (4), initialization vector (16), ciphertext (n), tag (32).
/// </para>
/// <para>
/// URL-safe encoding uses <see cref="Base64Url"/> rather than Essentials' Base64 provider, because
/// that provider emits standard base64 whose <c>+</c>, <c>/</c>, and <c>=</c> characters do not
/// belong unescaped in a URL.
/// </para>
/// </remarks>
/// <param name="encryption">The AES provider.</param>
/// <param name="options">The configured options, supplying the token keys.</param>
/// <param name="timeProvider">Clock, injected so expiry is testable.</param>
public sealed class HrefTokenCodec(
	IEncryptionProvider encryption,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider) : IHrefTokenCodec
{
	private const byte FormatVersion = 1;
	private const int KeyIdLength = 4;
	private const int IvLength = 16;
	private const int TagLength = 32;
	private const int HeaderLength = 1 + KeyIdLength + IvLength;

	/// <summary>The shortest possible token: header, one AES block, and a tag.</summary>
	private const int MinimumLength = HeaderLength + 16 + TagLength;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly DerivedKey[] _keys = [.. options.Value.TokenKeys.Select(DerivedKey.From)];

	/// <inheritdoc />
	public string Encode(HrefToken token)
	{
		Ensure.NotNull(token);

		DerivedKey key = _keys[0];
		byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(token, Json);
		byte[] iv = encryption.GenerateIV();
		byte[] ciphertext = encryption.Encrypt(plaintext, key.Encryption, iv);

		byte[] buffer = new byte[HeaderLength + ciphertext.Length + TagLength];
		buffer[0] = FormatVersion;
		key.Id.CopyTo(buffer.AsSpan(1, KeyIdLength));
		iv.CopyTo(buffer.AsSpan(1 + KeyIdLength, IvLength));
		ciphertext.CopyTo(buffer.AsSpan(HeaderLength, ciphertext.Length));

		// The tag covers the version byte and key id as well as the initialization vector and
		// ciphertext, so neither of those can be swapped for another key's.
		int authenticatedLength = HeaderLength + ciphertext.Length;
		HMACSHA256.HashData(
			key.Authentication,
			buffer.AsSpan(0, authenticatedLength),
			buffer.AsSpan(authenticatedLength));

		CryptographicOperations.ZeroMemory(plaintext);
		return Base64Url.EncodeToString(buffer);
	}

	/// <inheritdoc />
	public bool TryDecode(
		string? encoded,
		[NotNullWhen(true)] out HrefToken? token,
		out string? failureReason)
	{
		token = null;

		if (string.IsNullOrEmpty(encoded))
		{
			failureReason = "Token was absent.";
			return false;
		}

		byte[] buffer;

		try
		{
			buffer = Base64Url.DecodeFromChars(encoded);
		}
		catch (FormatException)
		{
			failureReason = "Token was not valid base64url.";
			return false;
		}

		if (buffer.Length < MinimumLength)
		{
			failureReason = "Token was shorter than the minimum valid length.";
			return false;
		}

		if (buffer[0] != FormatVersion)
		{
			failureReason = $"Token format version {buffer[0]} is not supported.";
			return false;
		}

		DerivedKey? key = FindKey(buffer.AsSpan(1, KeyIdLength));

		if (key is null)
		{
			failureReason = "Token was signed with a key that is not configured.";
			return false;
		}

		int authenticatedLength = buffer.Length - TagLength;
		Span<byte> expected = stackalloc byte[TagLength];
		HMACSHA256.HashData(key.Authentication, buffer.AsSpan(0, authenticatedLength), expected);

		// Verify before decrypting, which is what keeps CBC padding behavior out of reach.
		if (!CryptographicOperations.FixedTimeEquals(expected, buffer.AsSpan(authenticatedLength, TagLength)))
		{
			failureReason = "Token failed authentication.";
			return false;
		}

		byte[] plaintext;

		try
		{
			plaintext = encryption.Decrypt(
				buffer.AsSpan(HeaderLength, authenticatedLength - HeaderLength),
				key.Encryption,
				buffer.AsSpan(1 + KeyIdLength, IvLength));
		}
		catch (CryptographicException)
		{
			failureReason = "Token could not be decrypted.";
			return false;
		}

		HrefToken? candidate;

		try
		{
			candidate = JsonSerializer.Deserialize<HrefToken>(plaintext, Json);
		}
		catch (JsonException)
		{
			failureReason = "Token payload was not valid JSON.";
			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
		}

		if (candidate is null)
		{
			failureReason = "Token payload was empty.";
			return false;
		}

		if (candidate.ExpiresAt <= timeProvider.GetUtcNow())
		{
			failureReason = "Token has expired.";
			return false;
		}

		token = candidate;
		failureReason = null;
		return true;
	}

	private DerivedKey? FindKey(ReadOnlySpan<byte> keyId)
	{
		foreach (DerivedKey candidate in _keys)
		{
			if (CryptographicOperations.FixedTimeEquals(candidate.Id, keyId))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// One configured key, split into the independent subkeys the construction needs.
	/// </summary>
	/// <remarks>
	/// The identifier is derived from the master key rather than being its index in configuration, so
	/// it stays stable across rotation and reveals nothing about the key itself.
	/// </remarks>
	private sealed record DerivedKey(byte[] Id, byte[] Encryption, byte[] Authentication)
	{
		public static DerivedKey From(string base64Key)
		{
			byte[] master = Convert.FromBase64String(base64Key);

			try
			{
				return new DerivedKey(
					Derive(master, "gitlfscache-token-id", KeyIdLength),
					Derive(master, "gitlfscache-token-encryption", 32),
					Derive(master, "gitlfscache-token-authentication", 32));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(master);
			}
		}

		private static byte[] Derive(byte[] master, string info, int length) =>
			HKDF.DeriveKey(
				HashAlgorithmName.SHA256,
				master,
				length,
				salt: null,
				info: Encoding.UTF8.GetBytes(info));
	}
}
