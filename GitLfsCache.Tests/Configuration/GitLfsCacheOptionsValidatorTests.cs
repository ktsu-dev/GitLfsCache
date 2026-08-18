// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Configuration;

using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class GitLfsCacheOptionsValidatorTests
{
	/// <summary>
	/// A root that is fully qualified on whichever platform the suite runs on, since the validator
	/// now refuses anything the store's AbsoluteDirectoryPath conversion would reject.
	/// </summary>
	private static readonly string FullyQualifiedRoot = Path.Combine(
		Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString(),
		"gitlfscache");

	private static GitLfsCacheOptions Valid()
	{
		GitLfsCacheOptions options = new()
		{
			TokenLifetime = TimeSpan.FromHours(1),
			Store = new StoreOptions
			{
				Root = FullyQualifiedRoot,
				MaxSize = "500GB",
				LowWaterMark = 0.9,
				StagingMaxAge = TimeSpan.FromHours(6),
				MaintenanceInterval = TimeSpan.FromMinutes(5),
			},
			Fetch = new FetchOptions { FollowerTimeout = TimeSpan.FromMinutes(5) },
		};

		options.TokenKeys.Add(Convert.ToBase64String(new byte[32]));
		options.Upstreams["github"] = new UpstreamOptions { BaseUrl = new Uri("https://github.com") };
		return options;
	}

	private static ValidateOptionsResult Validate(GitLfsCacheOptions options) =>
		new GitLfsCacheOptionsValidator().Validate(null, options);

	[TestMethod]
	public void Validate_ValidOptions_Succeeds()
	{
		ValidateOptionsResult result = Validate(Valid());

		Assert.IsTrue(result.Succeeded, result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NoUpstreams_FailsNamingUpstreams()
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams.Clear();

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Upstreams", result.FailureMessage);
	}

	[TestMethod]
	[DataRow("not-a-url")]
	[DataRow("ftp://example.com")]
	[DataRow("/relative")]
	public void Validate_UpstreamBaseUrlNotAbsoluteHttp_Fails(string candidate)
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams["github"] = new UpstreamOptions
		{
			BaseUrl = new Uri(candidate, UriKind.RelativeOrAbsolute),
		};

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("github", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NoTokenKeys_FailsNamingTokenKeys()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenKeys.Clear();

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("TokenKeys", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_TokenKeyWrongLength_FailsNamingTheExpectedLength()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenKeys.Clear();
		options.TokenKeys.Add(Convert.ToBase64String(new byte[16]));

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("32", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_TokenKeyNotBase64_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenKeys.Clear();
		options.TokenKeys.Add("not base64 !!");

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
	}

	[TestMethod]
	public void Validate_UnparsableMaxSize_FailsNamingMaxSize()
	{
		GitLfsCacheOptions options = Valid();
		options.Store.MaxSize = "big";

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("MaxSize", result.FailureMessage);
	}

	[TestMethod]
	[DataRow(0.0)]
	[DataRow(1.0)]
	[DataRow(1.5)]
	[DataRow(-0.5)]
	public void Validate_LowWaterMarkOutOfRange_Fails(double lowWaterMark)
	{
		GitLfsCacheOptions options = Valid();
		options.Store.LowWaterMark = lowWaterMark;

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("LowWaterMark", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_RelativeStoreRoot_FailsNamingRoot()
	{
		GitLfsCacheOptions options = Valid();
		options.Store.Root = "relative/path";

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Root", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_EmptyStoreRoot_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.Store.Root = "   ";

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Root", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NonPositiveTokenLifetime_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenLifetime = TimeSpan.Zero;

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("TokenLifetime", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_MultipleProblems_ReportsAllOfThem()
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams.Clear();
		options.TokenKeys.Clear();

		ValidateOptionsResult result = Validate(options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Upstreams", result.FailureMessage);
		Assert.Contains("TokenKeys", result.FailureMessage);
	}
}
