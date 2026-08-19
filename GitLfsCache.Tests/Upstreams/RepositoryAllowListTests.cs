// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Upstreams;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class RepositoryAllowListTests
{
	private const string BatchPath = "studio/game.git/info/lfs/objects/batch";

	private static RepositoryAllowList AllowList(params string[] patterns)
	{
		GitLfsCacheOptions options = new();
		UpstreamOptions upstream = new() { BaseUrl = new Uri("https://github.com") };

		foreach (string pattern in patterns)
		{
			upstream.Repositories.Add(pattern);
		}

		options.Upstreams["github"] = upstream;

		return new RepositoryAllowList(Options.Create(options));
	}

	[TestMethod]
	public void IsAllowed_DoubleStar_AllowsEverything()
	{
		RepositoryAllowList allowList = AllowList("**");

		Assert.IsTrue(allowList.IsAllowed("github", BatchPath));
		Assert.IsTrue(allowList.IsAllowed("github", "anything/at/all"));
		Assert.IsTrue(allowList.IsAllowed("github", string.Empty));
	}

	[TestMethod]
	public void IsAllowed_PrefixWithDoubleStar_AllowsThatPrefixOnly()
	{
		RepositoryAllowList allowList = AllowList("studio/**");

		Assert.IsTrue(allowList.IsAllowed("github", BatchPath));
		Assert.IsFalse(allowList.IsAllowed("github", "someone-else/game.git/info/lfs/objects/batch"));
	}

	[TestMethod]
	public void IsAllowed_SingleStar_DoesNotCrossASegment()
	{
		// The documented reason a repository pattern normally ends in ** rather than *: a single star
		// stops at the segment boundary, so it cannot reach a real Git LFS path.
		RepositoryAllowList allowList = AllowList("studio/*");

		Assert.IsTrue(allowList.IsAllowed("github", "studio/game.git"));
		Assert.IsFalse(allowList.IsAllowed("github", BatchPath));
	}

	[TestMethod]
	public void IsAllowed_AnyPatternMatching_IsEnough()
	{
		RepositoryAllowList allowList = AllowList("tools/**", "studio/**");

		Assert.IsTrue(allowList.IsAllowed("github", BatchPath));
	}

	[TestMethod]
	public void IsAllowed_UnknownUpstream_IsRefused()
	{
		RepositoryAllowList allowList = AllowList("**");

		Assert.IsFalse(allowList.IsAllowed("gitlab", BatchPath));
	}

	[TestMethod]
	public void IsAllowed_NoPatterns_RefusesEverything()
	{
		// The validator refuses this configuration at startup, so it should never be reached. Failing
		// closed rather than open is what makes that a defence in depth rather than a single point.
		RepositoryAllowList allowList = AllowList();

		Assert.IsFalse(allowList.IsAllowed("github", BatchPath));
	}

	[TestMethod]
	public void IsAllowed_RegexMetacharacters_AreLiteral()
	{
		// The dot in "game.git" must match a dot and nothing else, or every pattern naming a
		// repository would quietly allow its neighbours.
		RepositoryAllowList allowList = AllowList("studio/game.git/**");

		Assert.IsTrue(allowList.IsAllowed("github", BatchPath));
		Assert.IsFalse(allowList.IsAllowed("github", "studio/gameXgit/info/lfs/objects/batch"));
	}

	[TestMethod]
	public void IsAllowed_DiffersOnlyByCase_IsAllowed()
	{
		// Forge repository names are case insensitive, so a pattern that fails only because someone
		// typed Studio is a support ticket rather than a control.
		RepositoryAllowList allowList = AllowList("studio/**");

		Assert.IsTrue(allowList.IsAllowed("github", "Studio/Game.git/info/lfs/objects/batch"));
	}

	[TestMethod]
	public void IsAllowed_UpstreamKeyCase_MatchesTheRegistry()
	{
		// UpstreamRegistry resolves keys case insensitively, so a path it resolved must not then be
		// refused here for the same spelling.
		RepositoryAllowList allowList = AllowList("**");

		Assert.IsTrue(allowList.IsAllowed("GitHub", BatchPath));
	}

	[TestMethod]
	[DataRow("/studio/game.git/info/lfs/objects/batch")]
	[DataRow("studio/game.git/info/lfs/objects/batch/")]
	public void IsAllowed_SurroundingSlashes_AreIgnored(string path)
	{
		RepositoryAllowList allowList = AllowList("studio/**");

		Assert.IsTrue(allowList.IsAllowed("github", path));
	}
}
