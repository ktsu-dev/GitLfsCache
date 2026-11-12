// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tool;

using System.Collections.Concurrent;
using ktsu.GitLfsCache.Tool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

[TestClass]
public class RequestLoggingTests
{
	private const string Token = "secret-token-value";

	[TestMethod]
	public async Task TransferToken_WithNoAppSettings_IsNotLogged()
	{
		// The tool run from a directory with no appsettings.json logged ASP.NET Core's "Request starting"
		// line at Information, with the transfer URL's ?t= token in it (issue #167).
		IReadOnlyCollection<string> messages = await LogOneTransferRequestAsync(configuration: null);

		Assert.AreNotEqual(0, messages.Count, "Precondition: the host logged something.");
		Assert.IsFalse(
			messages.Any(m => m.Contains(Token, StringComparison.Ordinal)),
			string.Join(Environment.NewLine, messages));
	}

	[TestMethod]
	public async Task TransferToken_WithConfigurationAskingForRequestLogs_IsStillNotLogged()
	{
		// The filter has to hold over configuration that turns ASP.NET Core's logging back up.
		IReadOnlyCollection<string> messages = await LogOneTransferRequestAsync(
			new() { ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information" });

		Assert.IsFalse(
			messages.Any(m => m.Contains(Token, StringComparison.Ordinal)),
			string.Join(Environment.NewLine, messages));
	}

	/// <summary>
	/// Starts a host the way the tool configures its logging, from a content root with no
	/// appsettings.json, sends one request for a transfer URL, and returns every message it logged.
	/// </summary>
	private static async Task<IReadOnlyCollection<string>> LogOneTransferRequestAsync(Dictionary<string, string?>? configuration)
	{
		string contentRoot = Directory.CreateTempSubdirectory("gitlfscache-logging-").FullName;
		using RecordingLoggerProvider records = new();
		try
		{
			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				Args = [],
				ContentRootPath = contentRoot,
			});

			foreach ((string key, string? value) in configuration ?? [])
			{
				builder.Configuration[key] = value;
			}

			builder.WebHost.UseTestServer();
			Program.KeepTransferTokensOutOfLogs(builder.Logging);
			builder.Logging.AddProvider(records);

			await using WebApplication app = builder.Build();
			app.MapGet("/objects/{oid}", (string oid) => Results.Ok(oid));
			await app.StartAsync();

			using HttpClient client = app.GetTestClient();
			using HttpResponseMessage response = await client.GetAsync($"/objects/abc123?t={Token}");
			Assert.IsTrue(response.IsSuccessStatusCode, "Precondition: the request reached the endpoint.");
			await app.StopAsync();

			return [.. records.Messages];
		}
		finally
		{
			Directory.Delete(contentRoot, recursive: true);
		}
	}

	private sealed class RecordingLoggerProvider : ILoggerProvider
	{
		public ConcurrentQueue<string> Messages { get; } = new();

		public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Messages);

		public void Dispose()
		{
		}
	}

	private sealed class RecordingLogger(string category, ConcurrentQueue<string> messages) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel,
			EventId eventId,
			TState state,
			Exception? exception,
			Func<TState, Exception?, string> formatter) =>
			messages.Enqueue($"{category}: {formatter(state, exception)}");
	}
}
