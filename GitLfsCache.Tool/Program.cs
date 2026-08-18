// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tool;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Entry point for the gitlfscache host.
/// </summary>
internal static class Program
{
	private static void Main(string[] args)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
		WebApplication app = builder.Build();
		app.MapGet("/healthz", () => Results.Ok("ok"));
		app.Run();
	}
}
