// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tool;

using ktsu.GitLfsCache.Tool;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

[TestClass]
public class ConfigReloadTests
{
	[TestMethod]
	public void HostOptions_DefaultAppSettingsSources_DoNotReloadOnChange()
	{
		// Reloading appsettings.json on change watches the whole content root, the working directory,
		// which on Linux took an inotify watch per directory beneath it and could use up the user's limit
		// (issue #166).
		WebApplicationBuilder builder = WebApplication.CreateBuilder(Program.HostOptions());

		FileConfigurationSource[] fileSources = [.. builder.Configuration.Sources.OfType<FileConfigurationSource>()];

		Assert.AreNotEqual(0, fileSources.Length, "Precondition: the builder adds its default appsettings.json sources.");
		foreach (FileConfigurationSource source in fileSources)
		{
			Assert.IsFalse(source.ReloadOnChange, $"{source.Path} should not reload on change.");
		}
	}

	[TestMethod]
	public void HostOptions_StillReadsAppSettingsFromTheWorkingDirectory()
	{
		// Turning reload off must not stop the working directory's appsettings.json being read.
		string contentRoot = Directory.CreateTempSubdirectory("gitlfscache-config-").FullName;
		try
		{
			File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), """{ "GitLfsCache": { "PublicBaseUrl": "https://lfs.example" } }""");
			WebApplicationOptions options = Program.HostOptions();
			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				Args = options.Args,
				ApplicationName = options.ApplicationName,
				ContentRootPath = contentRoot,
			});

			Assert.AreEqual("https://lfs.example", builder.Configuration["GitLfsCache:PublicBaseUrl"]);
		}
		finally
		{
			Directory.Delete(contentRoot, recursive: true);
		}
	}
}
