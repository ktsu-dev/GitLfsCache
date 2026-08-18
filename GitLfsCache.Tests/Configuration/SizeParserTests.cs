// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Configuration;

using ktsu.GitLfsCache.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class SizeParserTests
{
	[TestMethod]
	[DataRow("0", 0L)]
	[DataRow("512", 512L)]
	[DataRow("500B", 500L)]
	[DataRow("1KB", 1_000L)]
	[DataRow("1MB", 1_000_000L)]
	[DataRow("500GB", 500_000_000_000L)]
	[DataRow("2TB", 2_000_000_000_000L)]
	[DataRow("1G", 1_000_000_000L)]
	[DataRow("1Ki", 1_024L)]
	[DataRow("1KiB", 1_024L)]
	[DataRow("500Gi", 536_870_912_000L)]
	[DataRow("2Ti", 2_199_023_255_552L)]
	[DataRow("  4 GB  ", 4_000_000_000L)]
	[DataRow("4gb", 4_000_000_000L)]
	public void Parse_AcceptedForms_ReturnsExpectedBytes(string input, long expected)
	{
		Assert.AreEqual(expected, SizeParser.Parse(input));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("   ")]
	[DataRow("GB")]
	[DataRow("-1")]
	[DataRow("-5GB")]
	[DataRow("1.5GB")]
	[DataRow("1XB")]
	[DataRow("1GBB")]
	[DataRow("99999999999TB")]
	public void TryParse_RejectedForms_ReturnsFalseAndZero(string input)
	{
		bool parsed = SizeParser.TryParse(input, out long bytes);

		Assert.IsFalse(parsed);
		Assert.AreEqual(0L, bytes);
	}

	[TestMethod]
	public void TryParse_Null_ReturnsFalse()
	{
		Assert.IsFalse(SizeParser.TryParse(null, out long bytes));
		Assert.AreEqual(0L, bytes);
	}

	[TestMethod]
	public void Parse_Invalid_ThrowsFormatExceptionNamingTheValue()
	{
		FormatException exception = Assert.ThrowsExactly<FormatException>(() => SizeParser.Parse("1XB"));

		Assert.Contains("1XB", exception.Message);
	}
}
