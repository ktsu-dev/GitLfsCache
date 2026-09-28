// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tool;

using System.Text;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tool;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

[TestClass]
public class AllowFlagTests
{
	private const string ConfigFile = """
		{
		  "GitLfsCache": {
		    "Upstreams": {
		      "github": { "BaseUrl": "https://github.com", "Repositories": ["studio/**", "**"] },
		      "ado": { "BaseUrl": "https://dev.azure.com/org", "Repositories": ["project/**", "other/**"] }
		    }
		  }
		}
		""";

	/// <summary>
	/// Binds the options the way the tool does: a configuration file, then the flags over it.
	/// </summary>
	private static GitLfsCacheOptions Bind(params string[] allows)
	{
		Assert.IsTrue(
			Program.TryParseAllows(allows, out Dictionary<string, List<string>> allowLists, out string? invalid),
			invalid);

		using MemoryStream file = new(Encoding.UTF8.GetBytes(ConfigFile));
		IConfiguration configuration = new ConfigurationBuilder().AddJsonStream(file).Build();

		ServiceCollection services = new();
		services.AddOptions<GitLfsCacheOptions>().Bind(configuration.GetSection(GitLfsCacheOptions.SectionName));
		services.PostConfigure<GitLfsCacheOptions>(options => Program.ReplaceAllowLists(options, allowLists));

		using ServiceProvider provider = services.BuildServiceProvider();
		return provider.GetRequiredService<IOptions<GitLfsCacheOptions>>().Value;
	}

	private static void AssertRepositories(GitLfsCacheOptions options, string upstream, params string[] expected) =>
		CollectionAssert.AreEqual(expected, options.Upstreams[upstream].Repositories.ToArray());

	[TestMethod]
	public void Allow_FewerPatternsThanConfigured_ReplacesTheConfiguredList()
	{
		GitLfsCacheOptions options = Bind("github=only/**");

		AssertRepositories(options, "github", "only/**");
	}

	[TestMethod]
	public void Allow_Repeated_KeepsEveryPatternInOrder()
	{
		GitLfsCacheOptions options = Bind("github=a/**", "GitHub=b/**");

		AssertRepositories(options, "github", "a/**", "b/**");
	}

	[TestMethod]
	public void Allow_ForOneUpstream_LeavesTheOthersConfiguredList()
	{
		GitLfsCacheOptions options = Bind("github=only/**");

		AssertRepositories(options, "ado", "project/**", "other/**");
	}

	[TestMethod]
	public void Allow_ForAnUnconfiguredUpstream_AddsItWithThoseRepositories()
	{
		GitLfsCacheOptions options = Bind("gitlab=team/**");

		AssertRepositories(options, "gitlab", "team/**");
	}

	[TestMethod]
	public void NoAllow_KeepsTheConfiguredList()
	{
		GitLfsCacheOptions options = Bind();

		AssertRepositories(options, "github", "studio/**", "**");
	}

	[TestMethod]
	[DataRow("github")]
	[DataRow("=studio/**")]
	[DataRow("github=")]
	public void TryParseAllows_Malformed_ReportsTheEntry(string entry)
	{
		Assert.IsFalse(Program.TryParseAllows([entry], out _, out string? invalid));
		Assert.AreEqual(entry, invalid);
	}
}
