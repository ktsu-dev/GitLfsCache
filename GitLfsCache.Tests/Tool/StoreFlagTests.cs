// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tool;

using ktsu.GitLfsCache.Tool;

[TestClass]
public class StoreFlagTests
{
	[TestMethod]
	public void Store_RelativeDirectory_ResolvesAgainstTheWorkingDirectory()
	{
		// The options validator refuses a root that is not fully qualified, so passing `--store ./cache`
		// through verbatim aborted startup with a validation stack trace.
		string resolved = Program.ResolveStoreRoot("./cache");

		Assert.IsTrue(Path.IsPathFullyQualified(resolved), resolved);
		Assert.AreEqual(Path.Combine(Environment.CurrentDirectory, "cache"), resolved);
	}

	[TestMethod]
	public async Task Store_RelativeDirectory_IsAcceptedByTheCommandLine()
	{
		// The invalid upstream stops the run after the flags are read and before a server starts, so a
		// relative --store reaches the same one-line failure path as any other flag mistake.
		int exitCode = await Program.Main(["--store", "./cache", "--upstream", "not-a-name-url-pair"]);

		Assert.AreEqual(1, exitCode);
	}

	[TestMethod]
	public void Store_FullyQualifiedDirectory_IsKeptAsGiven()
	{
		string root = Path.Combine(
			Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString(),
			"gitlfscache");

		Assert.AreEqual(root, Program.ResolveStoreRoot(root));
	}
}
