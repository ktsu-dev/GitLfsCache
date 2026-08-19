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

		UpstreamOptions upstream = new() { BaseUrl = new Uri("https://github.com") };
		upstream.Repositories.Add("**");
		options.Upstreams["github"] = upstream;

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

	[TestMethod]
	public void Validate_StoreDisabled_DoesNotRequireStoreSettings()
	{
		// A metadata-only deployment has no root, no budget and no sweep, so none of those are checked.
		GitLfsCacheOptions options = Valid();
		options.Store.Enabled = false;
		options.Store.Root = string.Empty;
		options.Store.MaxSize = "nonsense";
		options.Store.LowWaterMark = 99;

		Assert.IsTrue(Validate(options).Succeeded, Validate(options).FailureMessage);
	}

	[TestMethod]
	public void Validate_StoreAndLocksBothDisabled_Fails()
	{
		// Relaying every route with nothing terminated is a proxy that only adds a network hop.
		GitLfsCacheOptions options = Valid();
		options.Store.Enabled = false;
		options.Locks.Enabled = false;

		ValidateOptionsResult result = Validate(options);

		Assert.IsFalse(result.Succeeded);
		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Store:Enabled", result.FailureMessage);
		Assert.Contains("Locks:Enabled", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_ListTtlLongerThanAdmissionTtl_Fails()
	{
		// A listing served for longer than the authorization proving it may be read would outlive the
		// only evidence the caller was ever allowed to see it.
		GitLfsCacheOptions options = Valid();
		options.Locks.ListTtl = TimeSpan.FromMinutes(5);
		options.Locks.AdmissionTtl = TimeSpan.FromMinutes(1);

		ValidateOptionsResult result = Validate(options);

		Assert.IsFalse(result.Succeeded);
		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Locks:ListTtl", result.FailureMessage);
		Assert.Contains("Locks:AdmissionTtl", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_ListTtlEqualToAdmissionTtl_Succeeds()
	{
		GitLfsCacheOptions options = Valid();
		options.Locks.ListTtl = TimeSpan.FromMinutes(1);
		options.Locks.AdmissionTtl = TimeSpan.FromMinutes(1);

		Assert.IsTrue(Validate(options).Succeeded);
	}

	[TestMethod]
	[DataRow("ListTtl")]
	[DataRow("AdmissionTtl")]
	[DataRow("RefreshTimeout")]
	public void Validate_NonPositiveLockDuration_FailsNamingIt(string setting)
	{
		GitLfsCacheOptions options = Valid();

		switch (setting)
		{
			case "ListTtl":
				options.Locks.ListTtl = TimeSpan.Zero;
				break;
			case "AdmissionTtl":
				options.Locks.AdmissionTtl = TimeSpan.Zero;
				break;
			default:
				options.Locks.RefreshTimeout = TimeSpan.Zero;
				break;
		}

		ValidateOptionsResult result = Validate(options);

		Assert.IsFalse(result.Succeeded);
		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains($"Locks:{setting}", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NonPositiveMaxSnapshotLocks_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.Locks.MaxSnapshotLocks = 0;

		ValidateOptionsResult result = Validate(options);

		Assert.IsFalse(result.Succeeded);
		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Locks:MaxSnapshotLocks", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_UpstreamWithNoRepositories_FailsNamingTheWildcard()
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams["github"].Repositories.Clear();

		ValidateOptionsResult result = Validate(options);

		Assert.IsFalse(result.Succeeded);
		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Upstreams:github:Repositories", result.FailureMessage);

		// An operator who does want every repository should learn how from the message itself, since
		// the alternative is guessing or reading the source.
		Assert.Contains("**", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_EmptyRepositoryPattern_FailsNamingTheIndex()
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams["github"].Repositories.Add("   ");

		ValidateOptionsResult result = Validate(options);

		Assert.IsFalse(result.Succeeded);
		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Repositories[1]", result.FailureMessage);
	}
}
