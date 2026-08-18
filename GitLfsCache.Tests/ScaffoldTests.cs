// Copyright (c) 2023-2026 ktsu.dev contributors

namespace ktsu.GitLfsCache.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class ScaffoldTests
{
	[TestMethod]
	public void CoreLibrary_IsReferencedAndLoads()
	{
		Assert.AreEqual("ktsu.GitLfsCache", Placeholder.Name);
	}
}
