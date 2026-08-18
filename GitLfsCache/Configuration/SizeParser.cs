// Copyright (c) 2023-2026 ktsu.dev contributors

namespace ktsu.GitLfsCache.Configuration;

using System.Globalization;

/// <summary>
/// Parses human-written byte sizes such as <c>500GB</c> and <c>500Gi</c>.
/// </summary>
/// <remarks>
/// Decimal suffixes are powers of 1000 and binary suffixes are powers of 1024, matching the
/// distinction Kubernetes resource quantities make, so a store budget can be written the same way
/// as the volume request it has to fit inside. Fractional values are rejected rather than rounded,
/// because a silently rounded cache budget is harder to diagnose than a startup failure.
/// </remarks>
public static class SizeParser
{
	private static readonly (string Suffix, long Multiplier)[] Suffixes =
	[
		// Longest first, so "KiB" is matched before "Ki" and "KB" before "B".
		("KIB", 1_024L),
		("MIB", 1_024L * 1_024),
		("GIB", 1_024L * 1_024 * 1_024),
		("TIB", 1_024L * 1_024 * 1_024 * 1_024),
		("KI", 1_024L),
		("MI", 1_024L * 1_024),
		("GI", 1_024L * 1_024 * 1_024),
		("TI", 1_024L * 1_024 * 1_024 * 1_024),
		("KB", 1_000L),
		("MB", 1_000_000L),
		("GB", 1_000_000_000L),
		("TB", 1_000_000_000_000L),
		("K", 1_000L),
		("M", 1_000_000L),
		("G", 1_000_000_000L),
		("T", 1_000_000_000_000L),
		("B", 1L),
	];

	/// <summary>
	/// Parses a byte size, returning false rather than throwing on malformed input.
	/// </summary>
	/// <param name="value">The value to parse, for example <c>500GB</c>.</param>
	/// <param name="bytes">The parsed size in bytes, or zero when parsing failed.</param>
	/// <returns><see langword="true"/> when <paramref name="value"/> was parsed.</returns>
	public static bool TryParse(string? value, out long bytes)
	{
		bytes = 0;

		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		string trimmed = value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
		long multiplier = 1;

		foreach ((string suffix, long candidate) in Suffixes)
		{
			if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
			{
				multiplier = candidate;
				trimmed = trimmed[..^suffix.Length];
				break;
			}
		}

		// NumberStyles.None is what rejects a sign and a decimal point without extra checks.
		if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long magnitude))
		{
			return false;
		}

		try
		{
			bytes = checked(magnitude * multiplier);
		}
		catch (OverflowException)
		{
			bytes = 0;
			return false;
		}

		return true;
	}

	/// <summary>
	/// Parses a byte size, throwing on malformed input.
	/// </summary>
	/// <param name="value">The value to parse, for example <c>500GB</c>.</param>
	/// <returns>The parsed size in bytes.</returns>
	/// <exception cref="FormatException">The value is not a recognized byte size.</exception>
	public static long Parse(string value)
	{
		return TryParse(value, out long bytes)
			? bytes
			: throw new FormatException(
				$"'{value}' is not a valid byte size. Use a whole number with an optional suffix, for example 500GB or 500Gi.");
	}
}
