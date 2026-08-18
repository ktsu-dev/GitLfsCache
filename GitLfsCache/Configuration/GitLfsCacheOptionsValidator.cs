// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

using Microsoft.Extensions.Options;

/// <summary>
/// Validates <see cref="GitLfsCacheOptions"/> at startup.
/// </summary>
/// <remarks>
/// Every problem is collected rather than reported one at a time, because an operator fixing
/// configuration by trial and error across restarts is a poor use of their afternoon. Store
/// writability is checked separately, since touching the filesystem from an options validator runs
/// at an awkward point in the host lifetime.
/// </remarks>
public sealed class GitLfsCacheOptionsValidator : IValidateOptions<GitLfsCacheOptions>
{
	/// <summary>
	/// The exact decoded length a token key must have.
	/// </summary>
	public const int RequiredKeyLengthBytes = 32;

	/// <inheritdoc />
	public ValidateOptionsResult Validate(string? name, GitLfsCacheOptions options)
	{
		Ensure.NotNull(options);

		List<string> failures = [];

		if (options.Upstreams.Count == 0)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Upstreams must contain at least one upstream.");
		}

		foreach ((string key, UpstreamOptions upstream) in options.Upstreams)
		{
			if (!IsAbsoluteHttpUrl(upstream.BaseUrl))
			{
				failures.Add(
					$"{GitLfsCacheOptions.SectionName}:Upstreams:{key}:BaseUrl must be an absolute http or https URL, but was '{upstream.BaseUrl}'.");
			}
		}

		if (options.PublicBaseUrl is not null && !IsAbsoluteHttpUrl(options.PublicBaseUrl))
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:PublicBaseUrl must be an absolute http or https URL when set, but was '{options.PublicBaseUrl}'.");
		}

		if (options.TokenKeys.Count == 0)
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:TokenKeys must contain at least one base64 encoded {RequiredKeyLengthBytes} byte key.");
		}

		for (int index = 0; index < options.TokenKeys.Count; index++)
		{
			if (!TryDecodeKey(options.TokenKeys[index], out int length))
			{
				failures.Add($"{GitLfsCacheOptions.SectionName}:TokenKeys[{index}] is not valid base64.");
			}
			else if (length != RequiredKeyLengthBytes)
			{
				failures.Add(
					$"{GitLfsCacheOptions.SectionName}:TokenKeys[{index}] decodes to {length} bytes but must be {RequiredKeyLengthBytes} bytes.");
			}
		}

		if (options.TokenLifetime <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:TokenLifetime must be greater than zero.");
		}

		if (string.IsNullOrWhiteSpace(options.Store.Root))
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Store:Root must be set to an absolute directory path.");
		}

		if (!SizeParser.TryParse(options.Store.MaxSize, out long maxSizeBytes) || maxSizeBytes <= 0)
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:Store:MaxSize must be a positive byte size such as 500GB or 500Gi, but was '{options.Store.MaxSize}'.");
		}

		if (options.Store.LowWaterMark is <= 0 or >= 1)
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:Store:LowWaterMark must be greater than zero and less than one, but was {options.Store.LowWaterMark}.");
		}

		if (options.Store.StagingMaxAge <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Store:StagingMaxAge must be greater than zero.");
		}

		if (options.Store.MaintenanceInterval <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Store:MaintenanceInterval must be greater than zero.");
		}

		if (options.Fetch.FollowerTimeout <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Fetch:FollowerTimeout must be greater than zero.");
		}

		return failures.Count == 0
			? ValidateOptionsResult.Success
			: ValidateOptionsResult.Fail(failures);
	}

	/// <summary>
	/// Reports whether a bound URL is absolute and addressable over HTTP.
	/// </summary>
	/// <remarks>
	/// The configuration binder accepts almost any string as a relative URI, so a typo lands here as
	/// a relative value rather than failing during binding. That is deliberate: it means the
	/// operator gets this message, naming the setting, rather than a binder exception.
	/// </remarks>
	private static bool IsAbsoluteHttpUrl(Uri? candidate) =>
		candidate is not null
		&& candidate.IsAbsoluteUri
		&& (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps);

	/// <summary>
	/// Decodes a candidate key to learn its length.
	/// </summary>
	/// <remarks>
	/// A key longer than the buffer reports as invalid base64 rather than reporting its length. That
	/// is acceptable: the message still refuses the key and names the setting it came from.
	/// </remarks>
	private static bool TryDecodeKey(string candidate, out int length)
	{
		length = 0;
		Span<byte> buffer = stackalloc byte[64];

		if (Convert.TryFromBase64String(candidate, buffer, out int written))
		{
			length = written;
			return true;
		}

		return false;
	}
}
