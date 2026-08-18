# ktsu.GitLfsCache Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a caching Git LFS proxy that relays every Batch API call upstream, rewrites the returned transfer URLs to point at itself, and serves object bytes from a local content-addressed store, shipping as both a dotnet tool and a container image for Kubernetes.

**Architecture:** A core library (`ktsu.GitLfsCache`) owns the domain and the ASP.NET Core endpoint mapping, exposed through `AddGitLfsCache` and `MapGitLfsCache`. A thin host project (`ktsu.GitLfsCache.Tool`) is simultaneously the dotnet tool payload and the container entrypoint, so the two cannot drift. Object transfer URLs carry an encrypted, authenticated token holding the upstream action, which makes replicas stateless and makes possession of a token proof that upstream authorized that object.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `ktsu.Sdk` and `ktsu.Sdk.Tool`, `ktsu.Essentials` (AES encryption, SHA256 hashing, filesystem abstraction, JSON serialization), `ktsu.Semantics.Paths`, `System.CommandLine`, MSTest via `MSTest.Sdk`, `Testably.Abstractions.Testing`, .NET SDK built-in container publishing, kustomize.

**Spec:** `docs/superpowers/specs/2026-08-18-gitlfscache-design.md`

## Global Constraints

Every task's requirements implicitly include this section.

- Tabs for indentation in C# files, never spaces. CRLF line endings.
- File-scoped namespaces (`namespace ktsu.GitLfsCache;`), with `using` directives placed *after* the namespace declaration.
- Braces on all control flow statements, always. Explicit accessibility modifiers on every member.
- No `this.` qualifiers. Nullable reference types enabled. Warnings treated as errors.
- Copyright header on every file: `// Copyright (c) 2023-2026 ktsu-dev contributors`
- No global suppressions, including in project properties. Use targeted `[SuppressMessage]` with a justification, falling back to a commented preprocessor directive only if no attribute exists.
- `Ensure.NotNull()` from Polyfill for parameter validation. Prefer Polyfill over custom polyfills.
- **Never run `dotnet format`.** It corrupts multi-target projects and this repository forbids it.
- MSTest with semantic asserts (`Assert.AreEqual`, `Assert.IsNotNull`, `Assert.ThrowsExactly<T>`, `Assert.HasCount`) rather than `Assert.IsTrue`/`IsFalse`, except where a `Try*` pattern's boolean return leaves no alternative.
- Commit messages carry a version marker: `[major]`, `[minor]`, `[patch]`, or `[pre]`. No `Co-Authored-By` lines.
- Never hand-edit `VERSION.md`, `CHANGELOG.md`, or `LICENSE.md`. They are generated.
- Target framework is `net10.0` only for all three projects. The core SDK's eight-framework default is explicitly overridden.
- Do **not** use `CreateProject/create-new-project.ps1`. It is stale: old-style outer SDK attributes, `.sln` instead of `.slnx`, wrong project names, and it runs `dotnet format`. Scaffold against the `KtsuBuild` layout instead.

**Pinned versions** (verify each against the feed in Task 1, Step 2, and bump if a newer stable exists):

| Package | Version |
|---|---|
| `ktsu.Sdk`, `ktsu.Sdk.Tool` | `2.26.0` |
| `MSTest.Sdk` | `4.3.3` |
| .NET SDK | `10.0.100`, `rollForward: latestFeature` |
| `ktsu.Essentials` | `1.2.2` |
| `ktsu.Semantics.Paths` | `2.9.4` |
| `Polyfill` | `11.0.2` |
| `System.CommandLine` | `2.0.11` |
| `Testably.Abstractions.Testing` | latest stable at scaffold time |

---

## File Structure

Locked before tasks so decomposition decisions do not drift.

```
GitLfsCache/
  GitLfsCache/                              core library, net10.0
    Configuration/
      GitLfsCacheOptions.cs                 root options, section name constant
      StoreOptions.cs                       root, max size, low water mark, staging max age
      FetchOptions.cs                       follower timeout
      UpstreamOptions.cs                    base url for one upstream
      GitLfsCacheOptionsValidator.cs        IValidateOptions, fails startup on bad config
      SizeParser.cs                         "500GB" and "500Gi" to bytes
      StartupChecks.cs                      IHostedService, refuses to start on unwritable store
    Upstreams/
      IUpstreamRegistry.cs                  key to base url resolution
      UpstreamRegistry.cs
      UpstreamClient.cs                     relays batch, opens fetches and upload relays
      IUpstreamClient.cs
    Tokens/
      HrefToken.cs                          payload record
      IHrefTokenCodec.cs
      HrefTokenCodec.cs                     encrypt-then-authenticate, key rotation
      TokenAction.cs                        download / upload / verify constants
    Batch/
      BatchRewriteContext.cs
      BatchRewriter.cs                      pure JsonNode transform
    Storage/
      IObjectStore.cs
      ObjectStore.cs                        content-addressed tree over IFileSystemProvider
      StagingHandle.cs
      StoredObject.cs
      TeeStream.cs                          one source to two sinks, store sink fails soft
      IEvictionPolicy.cs
      LeastRecentlyUsedEvictionPolicy.cs
      StoreMaintenanceService.cs            BackgroundService: eviction sweep, staging cleanup
    Fetching/
      IFetchCoalescer.cs
      FetchCoalescer.cs                     one upstream fetch per oid in flight
    Endpoints/
      BatchEndpoint.cs
      ObjectDownloadEndpoint.cs
      ObjectUploadEndpoint.cs
      VerifyEndpoint.cs
      RelayEndpoint.cs                      catch-all passthrough
      HealthEndpoints.cs                    /healthz and /readyz
      PublicUrlResolver.cs                  PublicBaseUrl or forwarded headers
    Observability/
      CacheMetrics.cs                       System.Diagnostics.Metrics counters
    GitLfsCacheServiceCollectionExtensions.cs   AddGitLfsCache
    GitLfsCacheEndpointRouteBuilderExtensions.cs MapGitLfsCache
  GitLfsCache.Tool/
    Program.cs                              System.CommandLine, builds and runs the host
    appsettings.json
  GitLfsCache.Tests/
    Configuration/SizeParserTests.cs
    Configuration/GitLfsCacheOptionsValidatorTests.cs
    Tokens/HrefTokenCodecTests.cs
    Batch/BatchRewriterTests.cs
    Storage/ObjectStoreTests.cs
    Storage/TeeStreamTests.cs
    Storage/EvictionTests.cs
    Fetching/FetchCoalescerTests.cs
    Integration/ProxyFlowTests.cs
    Integration/StubUpstream.cs             in-process fake LFS server
    TestData/                               captured GitHub and Azure DevOps batch payloads
  deploy/k8s/                               kustomize base
  docs/superpowers/{specs,plans}/
  .github/workflows/
  Directory.Packages.props
  GitLfsCache.slnx
  global.json
```

---

## Task 1: Scaffold, solution, and the build-risk check

The one real build risk in the spec is settled first: `ktsu.Sdk.Tool` clears `RuntimeIdentifiers` because `PackAsTool` turns each into a separate racing package, while container publishing needs a runtime identifier. This task proves `dotnet pack` and `PublishContainer` coexist in one project before any domain code exists. If they fight, the fallback is a second host project sharing `Program.cs` by file link, and that decision is made here rather than at the end.

**Files:**
- Create: `global.json`, `Directory.Packages.props`, `GitLfsCache.slnx`, `.gitignore`
- Create: `GitLfsCache/GitLfsCache.csproj`, `GitLfsCache/Placeholder.cs`
- Create: `GitLfsCache.Tool/GitLfsCache.Tool.csproj`, `GitLfsCache.Tool/Program.cs`, `GitLfsCache.Tool/appsettings.json`
- Create: `GitLfsCache.Tests/GitLfsCache.Tests.csproj`, `GitLfsCache.Tests/ScaffoldTests.cs`
- Create: `AUTHORS.md`, `DESCRIPTION.md`, `TAGS.md`, `README.md`, `PROJECT_URL.url`, `AUTHORS.url`
- Copy: `.github/workflows/dotnet.yml` and `.github/workflows/dependabot-merge.yml` from `../KtsuBuild/.github/workflows/`
- Copy: `scripts/` from `../KtsuBuild/scripts/`

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable three-project solution. Namespace root `ktsu.GitLfsCache`. The tool command is `gitlfscache`, derived by `ktsu.Sdk.Tool` from the solution name.

- [ ] **Step 1: Verify the repository is on `main` with only the spec committed**

Run: `git -C C:/dev/ktsu-dev/GitLfsCache log --oneline && git -C C:/dev/ktsu-dev/GitLfsCache status --short`
Expected: one commit adding the design spec, clean working tree.

- [ ] **Step 2: Confirm the pinned versions exist on the feed**

```bash
dotnet package search ktsu.Sdk --exact-match --format json
dotnet package search ktsu.Essentials --exact-match --format json
dotnet package search ktsu.Semantics.Paths --exact-match --format json
dotnet package search Testably.Abstractions.Testing --exact-match --format json
```

Record the latest stable version of each. Use those in Steps 3 and 4 rather than the table's values if they are newer. Do not use a prerelease.

- [ ] **Step 3: Write `global.json`**

Substitute the versions confirmed in Step 2.

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestFeature"
  },
  "msbuild-sdks": {
    "MSTest.Sdk": "4.3.3",
    "ktsu.Sdk": "2.26.0",
    "ktsu.Sdk.Tool": "2.26.0"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- [ ] **Step 4: Write `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Polyfill" Version="11.0.2" />
    <PackageVersion Include="ktsu.Essentials" Version="1.2.2" />
    <PackageVersion Include="ktsu.Essentials.EncryptionProviders.Aes" Version="1.2.2" />
    <PackageVersion Include="ktsu.Essentials.HashProviders.SHA256" Version="1.2.2" />
    <PackageVersion Include="ktsu.Essentials.FileSystemProviders.Native" Version="1.2.2" />
    <PackageVersion Include="ktsu.Essentials.SerializationProviders.Json" Version="1.2.2" />
    <PackageVersion Include="ktsu.Semantics.Paths" Version="2.9.4" />
    <PackageVersion Include="System.CommandLine" Version="2.0.11" />
    <PackageVersion Include="Testably.Abstractions.Testing" Version="9.0.0" />
  </ItemGroup>
</Project>
```

If `dotnet restore` later reports that a `ktsu.Essentials.*` sub-package id does not exist, list the real ids with `dotnet package search ktsu.Essentials --format json` and correct them. The category-and-implementation naming (`ktsu.Essentials.<Category>.<Impl>`) is documented in `../Essentials/README.md`.

- [ ] **Step 5: Write the three project files**

`GitLfsCache/GitLfsCache.csproj`. The `FrameworkReference` is what lets an ASP.NET Core component build under plain `Microsoft.NET.Sdk`, and the explicit single `TargetFramework` overrides the core SDK's eight-framework default.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="ktsu.Sdk" />

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Polyfill" PrivateAssets="all" />
    <PackageReference Include="ktsu.Essentials" />
    <PackageReference Include="ktsu.Essentials.EncryptionProviders.Aes" />
    <PackageReference Include="ktsu.Essentials.HashProviders.SHA256" />
    <PackageReference Include="ktsu.Essentials.FileSystemProviders.Native" />
    <PackageReference Include="ktsu.Essentials.SerializationProviders.Json" />
    <PackageReference Include="ktsu.Semantics.Paths" />
  </ItemGroup>
</Project>
```

`GitLfsCache.Tool/GitLfsCache.Tool.csproj`. Container properties are inline here rather than in an SDK variant, per the spec's sequencing decision.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Sdk Name="ktsu.Sdk" />
  <Sdk Name="ktsu.Sdk.Tool" />

  <PropertyGroup>
    <!-- Container publishing. ktsu.Sdk.Tool clears RuntimeIdentifiers because PackAsTool turns
         each entry into a separate racing package; ContainerRuntimeIdentifiers is a distinct
         property and drives the multi-architecture image index instead. -->
    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>
    <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled</ContainerBaseImage>
    <ContainerRuntimeIdentifiers>linux-x64;linux-arm64</ContainerRuntimeIdentifiers>
    <ContainerRepository>ktsu-dev/gitlfscache</ContainerRepository>
    <ContainerPort>8080</ContainerPort>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\GitLfsCache\GitLfsCache.csproj" />
    <PackageReference Include="Polyfill" PrivateAssets="all" />
    <PackageReference Include="System.CommandLine" />
  </ItemGroup>

  <ItemGroup>
    <!-- Microsoft.NET.Sdk does not glob appsettings the way Microsoft.NET.Sdk.Web does. -->
    <Content Include="appsettings*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`GitLfsCache.Tests/GitLfsCache.Tests.csproj`:

```xml
<Project Sdk="MSTest.Sdk">
  <Sdk Name="ktsu.Sdk" />

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <TargetFrameworks></TargetFrameworks>

    <!-- CA1707: underscores in test method names. CA1515: test classes must be public for
         MSTest discovery. CS1591: no XML docs required in test code. -->
    <NoWarn>$(NoWarn);CA1707;CA1515;CS1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Testably.Abstractions.Testing" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\GitLfsCache\GitLfsCache.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write `GitLfsCache.slnx`**

```xml
<Solution>
  <Configurations>
    <Platform Name="Any CPU" />
    <Platform Name="x64" />
  </Configurations>
  <Project Path="GitLfsCache/GitLfsCache.csproj" />
  <Project Path="GitLfsCache.Tool/GitLfsCache.Tool.csproj" />
  <Project Path="GitLfsCache.Tests/GitLfsCache.Tests.csproj" />
</Solution>
```

- [ ] **Step 7: Write the minimum source that proves the toolchain**

`GitLfsCache/Placeholder.cs`, deleted in Task 2 once real code exists:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache;

/// <summary>
/// Marker type proving the core library compiles. Removed once real types land.
/// </summary>
public static class Placeholder
{
	/// <summary>
	/// Gets the product name.
	/// </summary>
	public static string Name => "ktsu.GitLfsCache";
}
```

`GitLfsCache.Tool/Program.cs`, replaced properly in Task 15:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tool;

using Microsoft.AspNetCore.Builder;

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
```

`GitLfsCache.Tool/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "GitLfsCache": {
    "TokenLifetime": "01:00:00",
    "Store": {
      "MaxSize": "50GB",
      "LowWaterMark": 0.9,
      "StagingMaxAge": "06:00:00"
    },
    "Fetch": {
      "FollowerTimeout": "00:05:00"
    }
  }
}
```

`GitLfsCache.Tests/ScaffoldTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

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
```

- [ ] **Step 8: Write metadata files**

`AUTHORS.md`:

```markdown
ktsu.dev <admin@ktsu.dev>
```

`DESCRIPTION.md`:

```markdown
A caching reverse proxy for the Git LFS HTTP API. Relays every Batch API call upstream so upstream remains the sole authority on access, rewrites the returned transfer URLs to point at itself, and serves object bytes from a local content-addressed store. A cache miss is fetched from upstream once and stored on the way through. Ships as a dotnet tool and as a container image for Kubernetes.
```

`TAGS.md`:

```markdown
git-lfs lfs proxy cache caching reverse-proxy kubernetes dotnet-tool aspnetcore ci
```

`PROJECT_URL.url`:

```
[InternetShortcut]
URL=https://github.com/ktsu-dev/GitLfsCache
```

`AUTHORS.url`:

```
[InternetShortcut]
URL=https://github.com/ktsu-dev
```

`README.md` is a stub here and written properly in Task 18:

```markdown
# ktsu.GitLfsCache

> A caching reverse proxy for the Git LFS HTTP API.

Documentation is written in Task 18 of the implementation plan.
```

`.gitignore`: copy from `../KtsuBuild/.gitignore`.

- [ ] **Step 9: Restore, build, and run the scaffold test**

```bash
cd C:/dev/ktsu-dev/GitLfsCache
dotnet restore
dotnet build --no-incremental
dotnet test
```

Expected: restore succeeds, build succeeds with zero warnings (warnings are errors), one test passes.

If restore fails on a `ktsu.Essentials.*` package id, correct the ids per Step 4 and retry. If build fails because `FrameworkReference` conflicts with a core SDK property, capture the exact error before changing anything, since it decides whether the library can own the endpoints.

- [ ] **Step 10: Prove the tool packs**

```bash
dotnet pack GitLfsCache.Tool/GitLfsCache.Tool.csproj --configuration Release --output ./staging
```

Expected: exactly one package, `ktsu.GitLfsCache.Tool.<version>.nupkg`. Confirm it is a tool package and that the command name is `gitlfscache`:

```bash
unzip -p ./staging/ktsu.GitLfsCache.Tool.*.nupkg tools/net10.0/any/DotnetToolSettings.xml
```

Expected: a `<Command Name="gitlfscache" ...>` element, and the package must also contain the assemblies under `tools/net10.0/any/`. A package containing only `DotnetToolSettings.xml` means `IsPublishable` was not honored, which would install but fail at run time.

- [ ] **Step 11: Prove the container publishes**

```bash
dotnet publish GitLfsCache.Tool/GitLfsCache.Tool.csproj \
  --configuration Release \
  --runtime linux-x64 \
  /t:PublishContainer \
  -p:ContainerRegistry=
```

Expected: an image loaded into the local daemon, or a clear message naming the produced image. With no registry it targets the local Docker daemon, so if no daemon is available instead push to a local archive:

```bash
dotnet publish GitLfsCache.Tool/GitLfsCache.Tool.csproj \
  --configuration Release --runtime linux-x64 \
  /t:PublishContainer -p:ContainerArchiveOutputPath=./staging/image.tar.gz
```

Expected: `./staging/image.tar.gz` exists.

**This is the risk gate.** If `pack` and `PublishContainer` interfere (a racing intermediate directory, a runtime identifier conflict, or a tool package that loses its assemblies), stop and apply the spec's recorded fallback: add a `GitLfsCache.Service` host project holding the container properties, and link `Program.cs` from `GitLfsCache.Tool` with `<Compile Include="..\GitLfsCache.Tool\Program.cs" Link="Program.cs" />` so the two entry points cannot drift. Record which path was taken in the commit message.

- [ ] **Step 12: Commit**

```bash
cd C:/dev/ktsu-dev/GitLfsCache
git add .
git commit -m "[pre] Scaffold GitLfsCache solution, tool package, and container publish"
```

---

## Task 2: Size parser

Smallest pure unit, needed by configuration. Establishes the test rhythm.

**Files:**
- Create: `GitLfsCache/Configuration/SizeParser.cs`
- Create: `GitLfsCache.Tests/Configuration/SizeParserTests.cs`
- Delete: `GitLfsCache/Placeholder.cs`
- Modify: `GitLfsCache.Tests/ScaffoldTests.cs` (delete the file, its purpose is served)

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class SizeParser` with `public static bool TryParse(string? value, out long bytes)` and `public static long Parse(string value)`. `Parse` throws `FormatException` with the offending value in the message. Decimal suffixes (`KB`, `MB`, `GB`, `TB`, and bare `K`, `M`, `G`, `T`) are powers of 1000. Binary suffixes (`Ki`, `Mi`, `Gi`, `Ti`, `KiB`, `MiB`, `GiB`, `TiB`) are powers of 1024. Suffix matching is case-insensitive. A bare number is bytes.

- [ ] **Step 1: Write the failing tests**

`GitLfsCache.Tests/Configuration/SizeParserTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SizeParserTests"`
Expected: FAIL, the type or namespace `SizeParser` does not exist.

- [ ] **Step 3: Implement `SizeParser`**

`GitLfsCache/Configuration/SizeParser.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

using System.Globalization;

/// <summary>
/// Parses human-written byte sizes such as <c>500GB</c> and <c>500Gi</c>.
/// </summary>
/// <remarks>
/// Decimal suffixes are powers of 1000 and binary suffixes are powers of 1024, matching the
/// distinction Kubernetes resource quantities make. Fractional values are rejected rather than
/// rounded, because a silently rounded cache budget is harder to diagnose than a startup failure.
/// </remarks>
public static class SizeParser
{
	private static readonly (string Suffix, long Multiplier)[] Suffixes =
	[
		// Longest first, so "KiB" is matched before "K" and "KB" before "B".
		("KIB", 1_024L),
		("MIB", 1_024L * 1_024),
		("GIB", 1_024L * 1_024 * 1_024),
		("TIB", 1_024L * 1_024 * 1_024 * 1_024),
		("KI", 1_024L),
		("MI", 1_024L * 1_024),
		("GI", 1_024L * 1_024 * 1_024),
		("TI", 1_024L * 1_024 * 1_024 * 1_024),
		("KB", 1_000L),
		("MB", 1_000_000L),
		("GB", 1_000_000_000L),
		("TB", 1_000_000_000_000L),
		("K", 1_000L),
		("M", 1_000_000L),
		("G", 1_000_000_000L),
		("T", 1_000_000_000_000L),
		("B", 1L),
	];

	/// <summary>
	/// Parses a byte size, returning false rather than throwing on malformed input.
	/// </summary>
	/// <param name="value">The value to parse, for example <c>500GB</c>.</param>
	/// <param name="bytes">The parsed size in bytes, or zero when parsing failed.</param>
	/// <returns><see langword="true"/> when <paramref name="value"/> was parsed.</returns>
	public static bool TryParse(string? value, out long bytes)
	{
		bytes = 0;

		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		string trimmed = value.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
		long multiplier = 1;

		foreach ((string suffix, long candidate) in Suffixes)
		{
			if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
			{
				multiplier = candidate;
				trimmed = trimmed[..^suffix.Length];
				break;
			}
		}

		if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out long magnitude))
		{
			return false;
		}

		try
		{
			bytes = checked(magnitude * multiplier);
		}
		catch (OverflowException)
		{
			bytes = 0;
			return false;
		}

		return true;
	}

	/// <summary>
	/// Parses a byte size, throwing on malformed input.
	/// </summary>
	/// <param name="value">The value to parse, for example <c>500GB</c>.</param>
	/// <returns>The parsed size in bytes.</returns>
	/// <exception cref="FormatException">The value is not a recognized byte size.</exception>
	public static long Parse(string value)
	{
		return TryParse(value, out long bytes)
			? bytes
			: throw new FormatException(
				$"'{value}' is not a valid byte size. Use a whole number with an optional suffix, for example 500GB or 500Gi.");
	}
}
```

`NumberStyles.None` is what rejects `-1` and `1.5` without extra checks, since it forbids a sign and a decimal point.

- [ ] **Step 4: Remove the scaffold placeholders**

```bash
rm GitLfsCache/Placeholder.cs GitLfsCache.Tests/ScaffoldTests.cs
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SizeParserTests"`
Expected: PASS, 24 test cases.

- [ ] **Step 6: Run the whole suite and build clean**

Run: `dotnet build --no-incremental && dotnet test`
Expected: zero warnings, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add GitLfsCache/Configuration/SizeParser.cs GitLfsCache.Tests/Configuration/SizeParserTests.cs
git add -u
git commit -m "[minor] Add SizeParser for decimal and binary byte size suffixes"
```

---

## Task 3: Options, validation, and upstream registry

Configuration is the surface an operator gets wrong first, so it fails at startup with a message naming the problem rather than on the first clone.

**Files:**
- Create: `GitLfsCache/Configuration/GitLfsCacheOptions.cs`, `StoreOptions.cs`, `FetchOptions.cs`, `UpstreamOptions.cs`, `GitLfsCacheOptionsValidator.cs`
- Create: `GitLfsCache/Upstreams/IUpstreamRegistry.cs`, `UpstreamRegistry.cs`
- Create: `GitLfsCache.Tests/Configuration/GitLfsCacheOptionsValidatorTests.cs`
- Create: `GitLfsCache.Tests/Upstreams/UpstreamRegistryTests.cs`

**Interfaces:**
- Consumes: `SizeParser.TryParse` from Task 2.
- Produces:
  - `GitLfsCacheOptions` with `const string SectionName = "GitLfsCache"`, `string? PublicBaseUrl`, `IList<string> TokenKeys { get; }`, `TimeSpan TokenLifetime`, `StoreOptions Store`, `FetchOptions Fetch`, `IDictionary<string, UpstreamOptions> Upstreams { get; }` (ordinal-ignore-case keys).
  - `StoreOptions` with `string Root`, `string MaxSize`, `double LowWaterMark`, `TimeSpan StagingMaxAge`, and `long MaxSizeBytes => SizeParser.Parse(MaxSize)`.
  - `FetchOptions` with `TimeSpan FollowerTimeout`.
  - `UpstreamOptions` with `string BaseUrl`.
  - `IUpstreamRegistry` with `bool TryResolve(string key, out Uri? baseUrl)`.

Collection properties are getter-only with an initializer. `ConfigurationBinder` populates them in place, and `CA2227` (collection properties should be read only) would otherwise be a build error under warnings-as-errors.

- [ ] **Step 1: Write the failing validator tests**

`GitLfsCache.Tests/Configuration/GitLfsCacheOptionsValidatorTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Configuration;

using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class GitLfsCacheOptionsValidatorTests
{
	private static GitLfsCacheOptions Valid()
	{
		GitLfsCacheOptions options = new()
		{
			TokenLifetime = TimeSpan.FromHours(1),
			Store = new StoreOptions
			{
				Root = "/var/lib/gitlfscache",
				MaxSize = "500GB",
				LowWaterMark = 0.9,
				StagingMaxAge = TimeSpan.FromHours(6),
			},
			Fetch = new FetchOptions { FollowerTimeout = TimeSpan.FromMinutes(5) },
		};

		options.TokenKeys.Add(Convert.ToBase64String(new byte[32]));
		options.Upstreams["github"] = new UpstreamOptions { BaseUrl = "https://github.com" };
		return options;
	}

	[TestMethod]
	public void Validate_ValidOptions_Succeeds()
	{
		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, Valid());

		Assert.IsTrue(result.Succeeded, result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NoUpstreams_FailsNamingUpstreams()
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams.Clear();

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Upstreams", result.FailureMessage);
	}

	[TestMethod]
	[DataRow("not-a-url")]
	[DataRow("ftp://example.com")]
	[DataRow("/relative")]
	public void Validate_UpstreamBaseUrlNotAbsoluteHttp_Fails(string baseUrl)
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams["github"] = new UpstreamOptions { BaseUrl = baseUrl };

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("github", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NoTokenKeys_FailsNamingTokenKeys()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenKeys.Clear();

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("TokenKeys", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_TokenKeyWrongLength_FailsNamingTheExpectedLength()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenKeys.Clear();
		options.TokenKeys.Add(Convert.ToBase64String(new byte[16]));

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("32", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_TokenKeyNotBase64_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenKeys.Clear();
		options.TokenKeys.Add("not base64 !!");

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
	}

	[TestMethod]
	public void Validate_UnparsableMaxSize_FailsNamingMaxSize()
	{
		GitLfsCacheOptions options = Valid();
		options.Store.MaxSize = "big";

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

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

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("LowWaterMark", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_EmptyStoreRoot_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.Store.Root = "   ";

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Root", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_NonPositiveTokenLifetime_Fails()
	{
		GitLfsCacheOptions options = Valid();
		options.TokenLifetime = TimeSpan.Zero;

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("TokenLifetime", result.FailureMessage);
	}

	[TestMethod]
	public void Validate_MultipleProblems_ReportsAllOfThem()
	{
		GitLfsCacheOptions options = Valid();
		options.Upstreams.Clear();
		options.TokenKeys.Clear();

		ValidateOptionsResult result = new GitLfsCacheOptionsValidator().Validate(null, options);

		Assert.IsNotNull(result.FailureMessage);
		Assert.Contains("Upstreams", result.FailureMessage);
		Assert.Contains("TokenKeys", result.FailureMessage);
	}
}
```

- [ ] **Step 2: Write the failing registry tests**

`GitLfsCache.Tests/Upstreams/UpstreamRegistryTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Upstreams;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Upstreams;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class UpstreamRegistryTests
{
	private static UpstreamRegistry Registry()
	{
		GitLfsCacheOptions options = new();
		options.Upstreams["github"] = new UpstreamOptions { BaseUrl = "https://github.com" };
		options.Upstreams["ado"] = new UpstreamOptions { BaseUrl = "https://dev.azure.com/org" };
		return new UpstreamRegistry(Options.Create(options));
	}

	[TestMethod]
	public void TryResolve_KnownKey_ReturnsBaseUrl()
	{
		Assert.IsTrue(Registry().TryResolve("github", out Uri? baseUrl));
		Assert.AreEqual(new Uri("https://github.com"), baseUrl);
	}

	[TestMethod]
	public void TryResolve_KeyCasingDiffers_StillResolves()
	{
		Assert.IsTrue(Registry().TryResolve("GitHub", out Uri? baseUrl));
		Assert.AreEqual(new Uri("https://github.com"), baseUrl);
	}

	[TestMethod]
	public void TryResolve_UnknownKey_ReturnsFalseAndNull()
	{
		Assert.IsFalse(Registry().TryResolve("gitlab", out Uri? baseUrl));
		Assert.IsNull(baseUrl);
	}

	[TestMethod]
	public void TryResolve_KeyWithPathSegment_DoesNotResolve()
	{
		Assert.IsFalse(Registry().TryResolve("github/owner", out Uri? _));
	}
}
```

- [ ] **Step 3: Run both test classes to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GitLfsCacheOptionsValidatorTests|FullyQualifiedName~UpstreamRegistryTests"`
Expected: FAIL, the option types and registry do not exist.

- [ ] **Step 4: Implement the option types**

`GitLfsCache/Configuration/UpstreamOptions.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// One configured upstream Git LFS server.
/// </summary>
public sealed class UpstreamOptions
{
	/// <summary>
	/// Gets or sets the absolute base URL of the upstream, for example <c>https://github.com</c>.
	/// </summary>
	public string BaseUrl { get; set; } = string.Empty;
}
```

`GitLfsCache/Configuration/FetchOptions.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Settings governing upstream fetches on a cache miss.
/// </summary>
public sealed class FetchOptions
{
	/// <summary>
	/// Gets or sets how long a follower waits for the leader's fetch before fetching upstream itself.
	/// </summary>
	public TimeSpan FollowerTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
```

`GitLfsCache/Configuration/StoreOptions.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Settings for the local object store.
/// </summary>
public sealed class StoreOptions
{
	/// <summary>
	/// Gets or sets the absolute directory holding the object trees and staging areas.
	/// </summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>
	/// Gets or sets the byte budget, written with an optional suffix such as <c>500GB</c> or <c>500Gi</c>.
	/// </summary>
	public string MaxSize { get; set; } = "50GB";

	/// <summary>
	/// Gets or sets the fraction of the budget an eviction sweep reduces the store to, so that
	/// sweeps do not thrash at the boundary. Must be greater than zero and less than one.
	/// </summary>
	public double LowWaterMark { get; set; } = 0.9;

	/// <summary>
	/// Gets or sets how long an orphaned staging file survives before cleanup removes it.
	/// </summary>
	public TimeSpan StagingMaxAge { get; set; } = TimeSpan.FromHours(6);

	/// <summary>
	/// Gets the byte budget parsed from <see cref="MaxSize"/>.
	/// </summary>
	/// <exception cref="FormatException"><see cref="MaxSize"/> is not a valid byte size.</exception>
	public long MaxSizeBytes => SizeParser.Parse(MaxSize);
}
```

`GitLfsCache/Configuration/GitLfsCacheOptions.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

/// <summary>
/// Root configuration for the caching Git LFS proxy.
/// </summary>
/// <remarks>
/// Collection properties are getter-only with an initializer. The configuration binder populates
/// them in place, and a settable collection property would trip CA2227 under warnings-as-errors.
/// </remarks>
public sealed class GitLfsCacheOptions
{
	/// <summary>
	/// The configuration section these options bind from.
	/// </summary>
	public const string SectionName = "GitLfsCache";

	/// <summary>
	/// Gets or sets the externally reachable base URL used to build rewritten transfer URLs. When
	/// null, the request host and forwarded headers are used instead.
	/// </summary>
	public string? PublicBaseUrl { get; set; }

	/// <summary>
	/// Gets the base64 encoded 32 byte token keys. The first is used to encrypt, and every entry
	/// is tried when decrypting, so a key can be rotated without breaking transfers in flight.
	/// </summary>
	public IList<string> TokenKeys { get; } = [];

	/// <summary>
	/// Gets or sets how long a rewritten transfer URL remains valid.
	/// </summary>
	public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

	/// <summary>
	/// Gets or sets the object store settings.
	/// </summary>
	public StoreOptions Store { get; set; } = new();

	/// <summary>
	/// Gets or sets the upstream fetch settings.
	/// </summary>
	public FetchOptions Fetch { get; set; } = new();

	/// <summary>
	/// Gets the configured upstreams, keyed by the first path segment clients address them by.
	/// </summary>
	public IDictionary<string, UpstreamOptions> Upstreams { get; } =
		new Dictionary<string, UpstreamOptions>(StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 5: Implement the validator**

`GitLfsCache/Configuration/GitLfsCacheOptionsValidator.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Configuration;

using Microsoft.Extensions.Options;

/// <summary>
/// Validates <see cref="GitLfsCacheOptions"/> at startup.
/// </summary>
/// <remarks>
/// Every problem is collected rather than reported one at a time, because an operator fixing
/// configuration by trial and error across restarts is a poor use of their afternoon. Store
/// writability is checked separately by <see cref="StartupChecks"/>, since touching the
/// filesystem from an options validator runs at an awkward point in the host lifetime.
/// </remarks>
public sealed class GitLfsCacheOptionsValidator : IValidateOptions<GitLfsCacheOptions>
{
	private const int RequiredKeyLengthBytes = 32;

	/// <inheritdoc />
	public ValidateOptionsResult Validate(string? name, GitLfsCacheOptions options)
	{
		Ensure.NotNull(options);

		List<string> failures = [];

		if (options.Upstreams.Count == 0)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Upstreams must contain at least one upstream.");
		}

		foreach ((string key, UpstreamOptions upstream) in options.Upstreams)
		{
			if (!Uri.TryCreate(upstream.BaseUrl, UriKind.Absolute, out Uri? parsed)
				|| (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
			{
				failures.Add(
					$"{GitLfsCacheOptions.SectionName}:Upstreams:{key}:BaseUrl must be an absolute http or https URL, but was '{upstream.BaseUrl}'.");
			}
		}

		if (options.TokenKeys.Count == 0)
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:TokenKeys must contain at least one base64 encoded {RequiredKeyLengthBytes} byte key.");
		}

		for (int index = 0; index < options.TokenKeys.Count; index++)
		{
			string candidate = options.TokenKeys[index];

			if (!TryDecodeKey(candidate, out int length))
			{
				failures.Add($"{GitLfsCacheOptions.SectionName}:TokenKeys[{index}] is not valid base64.");
			}
			else if (length != RequiredKeyLengthBytes)
			{
				failures.Add(
					$"{GitLfsCacheOptions.SectionName}:TokenKeys[{index}] decodes to {length} bytes but must be {RequiredKeyLengthBytes} bytes.");
			}
		}

		if (options.TokenLifetime <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:TokenLifetime must be greater than zero.");
		}

		if (string.IsNullOrWhiteSpace(options.Store.Root))
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Store:Root must be set to an absolute directory path.");
		}

		if (!SizeParser.TryParse(options.Store.MaxSize, out long maxSizeBytes) || maxSizeBytes <= 0)
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:Store:MaxSize must be a positive byte size such as 500GB or 500Gi, but was '{options.Store.MaxSize}'.");
		}

		if (options.Store.LowWaterMark is <= 0 or >= 1)
		{
			failures.Add(
				$"{GitLfsCacheOptions.SectionName}:Store:LowWaterMark must be greater than zero and less than one, but was {options.Store.LowWaterMark}.");
		}

		if (options.Store.StagingMaxAge <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Store:StagingMaxAge must be greater than zero.");
		}

		if (options.Fetch.FollowerTimeout <= TimeSpan.Zero)
		{
			failures.Add($"{GitLfsCacheOptions.SectionName}:Fetch:FollowerTimeout must be greater than zero.");
		}

		return failures.Count == 0
			? ValidateOptionsResult.Success
			: ValidateOptionsResult.Fail(failures);
	}

	private static bool TryDecodeKey(string candidate, out int length)
	{
		length = 0;
		Span<byte> buffer = stackalloc byte[64];

		if (Convert.TryFromBase64String(candidate, buffer, out int written))
		{
			length = written;
			return true;
		}

		return false;
	}
}
```

A key longer than 64 bytes fails as "not valid base64" rather than reporting its length. That is acceptable: the message still refuses the key and points at the right setting.

- [ ] **Step 6: Implement the upstream registry**

`GitLfsCache/Upstreams/IUpstreamRegistry.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

/// <summary>
/// Resolves an upstream key taken from the request path to its configured base URL.
/// </summary>
public interface IUpstreamRegistry
{
	/// <summary>
	/// Resolves an upstream key.
	/// </summary>
	/// <param name="key">The first path segment of the request.</param>
	/// <param name="baseUrl">The configured base URL, or null when the key is unknown.</param>
	/// <returns><see langword="true"/> when the key is configured.</returns>
	public bool TryResolve(string key, out Uri? baseUrl);
}
```

`GitLfsCache/Upstreams/UpstreamRegistry.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Resolves upstream keys from <see cref="GitLfsCacheOptions.Upstreams"/>.
/// </summary>
/// <param name="options">The configured options.</param>
public sealed class UpstreamRegistry(IOptions<GitLfsCacheOptions> options) : IUpstreamRegistry
{
	private readonly Dictionary<string, Uri> _upstreams = options.Value.Upstreams.ToDictionary(
		pair => pair.Key,
		pair => new Uri(pair.Value.BaseUrl, UriKind.Absolute),
		StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public bool TryResolve(string key, out Uri? baseUrl)
	{
		baseUrl = null;

		if (string.IsNullOrEmpty(key) || key.Contains('/', StringComparison.Ordinal))
		{
			return false;
		}

		if (_upstreams.TryGetValue(key, out Uri? resolved))
		{
			baseUrl = resolved;
			return true;
		}

		return false;
	}
}
```

The constructor parses each `BaseUrl` eagerly, which is safe because the validator has already rejected anything unparsable, and it means the hot path never re-parses a URL.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~GitLfsCacheOptionsValidatorTests|FullyQualifiedName~UpstreamRegistryTests"`
Expected: PASS.

- [ ] **Step 8: Build clean and run the whole suite**

Run: `dotnet build --no-incremental && dotnet test`
Expected: zero warnings, all tests pass.

- [ ] **Step 9: Commit**

```bash
git add GitLfsCache/Configuration GitLfsCache/Upstreams GitLfsCache.Tests/Configuration GitLfsCache.Tests/Upstreams
git commit -m "[minor] Add configuration options, startup validation, and upstream registry"
```

---

## Task 4: Href token codec

The load-bearing security unit. AES alone is not enough: Essentials' provider uses `Aes.Create()` defaults, meaning CBC with PKCS7 and no authentication, and this token carries an upstream credential plus the oid that selects which bytes get served. It is encrypt-then-authenticate, with `HMACSHA256` and `HKDF` from the base class library because every Essentials hash provider is unkeyed.

**Files:**
- Create: `GitLfsCache/Tokens/TokenAction.cs`, `HrefToken.cs`, `IHrefTokenCodec.cs`, `HrefTokenCodec.cs`
- Create: `GitLfsCache.Tests/Tokens/HrefTokenCodecTests.cs`

**Interfaces:**
- Consumes: `GitLfsCacheOptions.TokenKeys`, `GitLfsCacheOptions.TokenLifetime` from Task 3.
- Produces:
  - `static class TokenAction` with `public const string Download = "download"`, `Upload = "upload"`, `Verify = "verify"`.
  - `sealed record HrefToken` with `required string Oid`, `required long Size`, `required string Upstream`, `required string Action`, `required string UpstreamHref`, `IReadOnlyDictionary<string, string> UpstreamHeaders { get; init; }`, `required DateTimeOffset ExpiresAt`.
  - `IHrefTokenCodec` with `string Encode(HrefToken token)` and `bool TryDecode(string? encoded, out HrefToken? token, out string? failureReason)`.
  - `HrefTokenCodec(IEncryptionProvider, IOptions<GitLfsCacheOptions>, TimeProvider)`.

The URL-safe encoding uses `System.Buffers.Text.Base64Url` from the base class library rather than Essentials' `Base64` provider, because the Essentials provider emits standard base64 with `+`, `/`, and `=`, none of which belong unescaped in a URL. This is a deliberate amendment to the spec's construction list.

- [ ] **Step 1: Write the failing tests**

`GitLfsCache.Tests/Tokens/HrefTokenCodecTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Tokens;

using ktsu.Essentials.EncryptionProviders;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class HrefTokenCodecTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	private static string Key(byte seed)
	{
		byte[] key = new byte[32];
		Array.Fill(key, seed);
		return Convert.ToBase64String(key);
	}

	private static (HrefTokenCodec Codec, FakeTimeProvider Time) Create(params string[] keys)
	{
		GitLfsCacheOptions options = new() { TokenLifetime = TimeSpan.FromHours(1) };

		foreach (string key in keys.Length == 0 ? [Key(1)] : keys)
		{
			options.TokenKeys.Add(key);
		}

		FakeTimeProvider time = new(Now);
		return (new HrefTokenCodec(new Aes(), Options.Create(options), time), time);
	}

	private static HrefToken Token() => new()
	{
		Oid = "9a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8",
		Size = 1234567,
		Upstream = "github",
		Action = TokenAction.Download,
		UpstreamHref = "https://objects.githubusercontent.com/really/long/signed/url?sig=abc",
		UpstreamHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer upstream-secret" },
		ExpiresAt = Now.AddHours(1),
	};

	[TestMethod]
	public void EncodeThenDecode_RoundTripsEveryField()
	{
		(HrefTokenCodec codec, _) = Create();
		HrefToken original = Token();

		string encoded = codec.Encode(original);

		Assert.IsTrue(codec.TryDecode(encoded, out HrefToken? decoded, out string? failure), failure);
		Assert.IsNotNull(decoded);
		Assert.AreEqual(original.Oid, decoded.Oid);
		Assert.AreEqual(original.Size, decoded.Size);
		Assert.AreEqual(original.Upstream, decoded.Upstream);
		Assert.AreEqual(original.Action, decoded.Action);
		Assert.AreEqual(original.UpstreamHref, decoded.UpstreamHref);
		Assert.AreEqual(original.ExpiresAt, decoded.ExpiresAt);
		Assert.AreEqual("Bearer upstream-secret", decoded.UpstreamHeaders["Authorization"]);
	}

	[TestMethod]
	public void Encode_ProducesUrlSafeOutput()
	{
		(HrefTokenCodec codec, _) = Create();

		string encoded = codec.Encode(Token());

		Assert.IsFalse(encoded.Contains('+', StringComparison.Ordinal));
		Assert.IsFalse(encoded.Contains('/', StringComparison.Ordinal));
		Assert.IsFalse(encoded.Contains('=', StringComparison.Ordinal));
		Assert.AreEqual(Uri.EscapeDataString(encoded), encoded);
	}

	[TestMethod]
	public void Encode_SameTokenTwice_ProducesDifferentCiphertext()
	{
		(HrefTokenCodec codec, _) = Create();
		HrefToken token = Token();

		Assert.AreNotEqual(codec.Encode(token), codec.Encode(token));
	}

	[TestMethod]
	public void TryDecode_AfterExpiry_FailsNamingExpiry()
	{
		(HrefTokenCodec codec, FakeTimeProvider time) = Create();
		string encoded = codec.Encode(Token());

		time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));

		Assert.IsFalse(codec.TryDecode(encoded, out HrefToken? decoded, out string? failure));
		Assert.IsNull(decoded);
		Assert.IsNotNull(failure);
		Assert.Contains("expired", failure, StringComparison.OrdinalIgnoreCase);
	}

	[TestMethod]
	public void TryDecode_TamperedByte_IsRejectedAtEveryOffset()
	{
		(HrefTokenCodec codec, _) = Create();
		byte[] raw = System.Buffers.Text.Base64Url.DecodeFromChars(codec.Encode(Token()));

		for (int offset = 0; offset < raw.Length; offset++)
		{
			byte[] tampered = [.. raw];
			tampered[offset] ^= 0x01;
			string encoded = System.Buffers.Text.Base64Url.EncodeToString(tampered);

			Assert.IsFalse(
				codec.TryDecode(encoded, out HrefToken? decoded, out string? _),
				$"A token tampered at byte {offset} was accepted.");
			Assert.IsNull(decoded);
		}
	}

	[TestMethod]
	public void TryDecode_TruncatedToken_IsRejected()
	{
		(HrefTokenCodec codec, _) = Create();
		string encoded = codec.Encode(Token());

		Assert.IsFalse(codec.TryDecode(encoded[..(encoded.Length / 2)], out HrefToken? _, out string? _));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("!!!not base64url!!!")]
	public void TryDecode_Garbage_IsRejectedWithoutThrowing(string encoded)
	{
		(HrefTokenCodec codec, _) = Create();

		Assert.IsFalse(codec.TryDecode(encoded, out HrefToken? _, out string? failure));
		Assert.IsNotNull(failure);
	}

	[TestMethod]
	public void TryDecode_Null_IsRejectedWithoutThrowing()
	{
		(HrefTokenCodec codec, _) = Create();

		Assert.IsFalse(codec.TryDecode(null, out HrefToken? _, out string? _));
	}

	[TestMethod]
	public void TryDecode_TokenFromRotatedOutKey_StillDecodes()
	{
		(HrefTokenCodec oldCodec, _) = Create(Key(2));
		string encoded = oldCodec.Encode(Token());

		// New key first, old key retained: encryption uses the new one, decryption tries both.
		(HrefTokenCodec rotated, _) = Create(Key(3), Key(2));

		Assert.IsTrue(rotated.TryDecode(encoded, out HrefToken? decoded, out string? failure), failure);
		Assert.IsNotNull(decoded);
	}

	[TestMethod]
	public void TryDecode_TokenFromAnUnknownKey_IsRejected()
	{
		(HrefTokenCodec foreign, _) = Create(Key(9));
		string encoded = foreign.Encode(Token());

		(HrefTokenCodec local, _) = Create(Key(1));

		Assert.IsFalse(local.TryDecode(encoded, out HrefToken? _, out string? failure));
		Assert.IsNotNull(failure);
	}

	[TestMethod]
	public void Encode_ThenDecode_WithEmptyHeaders_RoundTrips()
	{
		(HrefTokenCodec codec, _) = Create();
		HrefToken token = Token() with { UpstreamHeaders = new Dictionary<string, string>() };

		Assert.IsTrue(codec.TryDecode(codec.Encode(token), out HrefToken? decoded, out string? _));
		Assert.IsNotNull(decoded);
		Assert.HasCount(0, decoded.UpstreamHeaders);
	}
}
```

`FakeTimeProvider` comes from `Microsoft.Extensions.TimeProvider.Testing`. Add it to `Directory.Packages.props` and to the test project in Step 2.

- [ ] **Step 2: Add the test-time packages**

Add to `Directory.Packages.props`:

```xml
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.10.0" />
```

Add to `GitLfsCache.Tests/GitLfsCache.Tests.csproj`, in the existing `PackageReference` group:

```xml
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Confirm the version with `dotnet package search Microsoft.Extensions.TimeProvider.Testing --exact-match --format json` and use the latest stable.

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~HrefTokenCodecTests"`
Expected: FAIL, `HrefTokenCodec` does not exist.

- [ ] **Step 4: Implement the token types**

`GitLfsCache/Tokens/TokenAction.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

/// <summary>
/// The Git LFS batch action a token authorizes.
/// </summary>
/// <remarks>
/// A token names its action so a download token cannot be replayed against the upload endpoint,
/// which matters because both share the <c>/objects/{oid}</c> path and differ only by method.
/// </remarks>
public static class TokenAction
{
	/// <summary>Fetching an object.</summary>
	public const string Download = "download";

	/// <summary>Sending an object.</summary>
	public const string Upload = "upload";

	/// <summary>Confirming an upload landed.</summary>
	public const string Verify = "verify";
}
```

`GitLfsCache/Tokens/HrefToken.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

/// <summary>
/// The payload carried inside a rewritten transfer URL.
/// </summary>
/// <remarks>
/// Carrying the upstream action here rather than in server state is what makes replicas stateless
/// and what lets an object evicted between the batch call and the transfer still resolve.
/// </remarks>
public sealed record HrefToken
{
	/// <summary>Gets the object id, a lowercase hex SHA256 digest.</summary>
	public required string Oid { get; init; }

	/// <summary>Gets the object size in bytes as reported by upstream.</summary>
	public required long Size { get; init; }

	/// <summary>Gets the configured upstream key this object belongs to.</summary>
	public required string Upstream { get; init; }

	/// <summary>Gets the action this token authorizes. See <see cref="TokenAction"/>.</summary>
	public required string Action { get; init; }

	/// <summary>Gets the upstream URL this action was originally pointed at.</summary>
	public required string UpstreamHref { get; init; }

	/// <summary>Gets the headers upstream requires on the transfer, including any credential.</summary>
	public IReadOnlyDictionary<string, string> UpstreamHeaders { get; init; } =
		new Dictionary<string, string>();

	/// <summary>Gets the instant after which this token is refused.</summary>
	public required DateTimeOffset ExpiresAt { get; init; }
}
```

`GitLfsCache/Tokens/IHrefTokenCodec.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

/// <summary>
/// Encodes and decodes the opaque token carried by a rewritten transfer URL.
/// </summary>
public interface IHrefTokenCodec
{
	/// <summary>
	/// Encrypts, authenticates, and URL-safe encodes a token.
	/// </summary>
	/// <param name="token">The payload to encode.</param>
	/// <returns>A URL-safe string suitable for a query parameter value.</returns>
	public string Encode(HrefToken token);

	/// <summary>
	/// Decodes, authenticates, decrypts, and expiry-checks a token.
	/// </summary>
	/// <param name="encoded">The encoded token, or null.</param>
	/// <param name="token">The decoded payload, or null on any failure.</param>
	/// <param name="failureReason">Why decoding failed, for logging. Never returned to a client.</param>
	/// <returns><see langword="true"/> when the token is authentic and unexpired.</returns>
	public bool TryDecode(string? encoded, out HrefToken? token, out string? failureReason);
}
```

- [ ] **Step 5: Implement the codec**

`GitLfsCache/Tokens/HrefTokenCodec.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tokens;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ktsu.Essentials;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Encrypt-then-authenticate token codec over Essentials' AES provider.
/// </summary>
/// <remarks>
/// Essentials' AES provider uses <c>Aes.Create()</c> defaults, which is CBC with PKCS7 and no
/// authentication. A token that is only encrypted is malleable, and this one carries an upstream
/// credential and the oid selecting which bytes get served, so a message authentication code is
/// not optional. HMACSHA256 and HKDF come from the base class library because every Essentials
/// hash provider is unkeyed; a keyed-hash provider is a recorded gap to contribute back.
///
/// Wire format: version(1) | keyId(4) | iv(16) | ciphertext(n) | tag(32).
/// </remarks>
/// <param name="encryption">The AES provider.</param>
/// <param name="options">The configured options, supplying the token keys and lifetime.</param>
/// <param name="timeProvider">Clock, injected so expiry is testable.</param>
public sealed class HrefTokenCodec(
	IEncryptionProvider encryption,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider) : IHrefTokenCodec
{
	private const byte FormatVersion = 1;
	private const int KeyIdLength = 4;
	private const int IvLength = 16;
	private const int TagLength = 32;
	private const int HeaderLength = 1 + KeyIdLength + IvLength;
	private const int MinimumLength = HeaderLength + 16 + TagLength;

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly DerivedKey[] _keys = [.. options.Value.TokenKeys.Select(DerivedKey.From)];

	/// <inheritdoc />
	public string Encode(HrefToken token)
	{
		Ensure.NotNull(token);

		DerivedKey key = _keys[0];
		byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(token, Json);
		byte[] iv = encryption.GenerateIV();
		byte[] ciphertext = encryption.Encrypt(plaintext, key.Encryption, iv);

		byte[] buffer = new byte[HeaderLength + ciphertext.Length + TagLength];
		buffer[0] = FormatVersion;
		key.Id.CopyTo(buffer.AsSpan(1, KeyIdLength));
		iv.CopyTo(buffer.AsSpan(1 + KeyIdLength, IvLength));
		ciphertext.CopyTo(buffer.AsSpan(HeaderLength, ciphertext.Length));

		// Authenticate everything before the tag, so the version and key id are covered too.
		int authenticatedLength = HeaderLength + ciphertext.Length;
		HMACSHA256.HashData(key.Authentication, buffer.AsSpan(0, authenticatedLength), buffer.AsSpan(authenticatedLength));

		CryptographicOperations.ZeroMemory(plaintext);
		return Base64Url.EncodeToString(buffer);
	}

	/// <inheritdoc />
	public bool TryDecode(string? encoded, out HrefToken? token, out string? failureReason)
	{
		token = null;
		failureReason = null;

		if (string.IsNullOrEmpty(encoded))
		{
			failureReason = "Token was absent.";
			return false;
		}

		byte[] buffer;

		try
		{
			buffer = Base64Url.DecodeFromChars(encoded);
		}
		catch (FormatException)
		{
			failureReason = "Token was not valid base64url.";
			return false;
		}

		if (buffer.Length < MinimumLength)
		{
			failureReason = "Token was shorter than the minimum valid length.";
			return false;
		}

		if (buffer[0] != FormatVersion)
		{
			failureReason = $"Token format version {buffer[0]} is not supported.";
			return false;
		}

		ReadOnlySpan<byte> keyId = buffer.AsSpan(1, KeyIdLength);
		DerivedKey? key = null;

		foreach (DerivedKey candidate in _keys)
		{
			if (CryptographicOperations.FixedTimeEquals(candidate.Id, keyId))
			{
				key = candidate;
				break;
			}
		}

		if (key is null)
		{
			failureReason = "Token was signed with a key that is not configured.";
			return false;
		}

		int authenticatedLength = buffer.Length - TagLength;
		Span<byte> expected = stackalloc byte[TagLength];
		HMACSHA256.HashData(key.Authentication, buffer.AsSpan(0, authenticatedLength), expected);

		if (!CryptographicOperations.FixedTimeEquals(expected, buffer.AsSpan(authenticatedLength, TagLength)))
		{
			failureReason = "Token failed authentication.";
			return false;
		}

		byte[] plaintext;

		try
		{
			plaintext = encryption.Decrypt(
				buffer.AsSpan(HeaderLength, authenticatedLength - HeaderLength),
				key.Encryption,
				buffer.AsSpan(1 + KeyIdLength, IvLength));
		}
		catch (CryptographicException)
		{
			failureReason = "Token could not be decrypted.";
			return false;
		}

		HrefToken? candidate;

		try
		{
			candidate = JsonSerializer.Deserialize<HrefToken>(plaintext, Json);
		}
		catch (JsonException)
		{
			failureReason = "Token payload was not valid JSON.";
			return false;
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
		}

		if (candidate is null)
		{
			failureReason = "Token payload was empty.";
			return false;
		}

		if (candidate.ExpiresAt <= timeProvider.GetUtcNow())
		{
			failureReason = "Token has expired.";
			return false;
		}

		token = candidate;
		return true;
	}

	private sealed record DerivedKey(byte[] Id, byte[] Encryption, byte[] Authentication)
	{
		public static DerivedKey From(string base64Key)
		{
			byte[] master = Convert.FromBase64String(base64Key);

			try
			{
				return new DerivedKey(
					Derive(master, "gitlfscache-token-id", KeyIdLength),
					Derive(master, "gitlfscache-token-encryption", 32),
					Derive(master, "gitlfscache-token-authentication", 32));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(master);
			}
		}

		private static byte[] Derive(byte[] master, string info, int length) =>
			HKDF.DeriveKey(
				HashAlgorithmName.SHA256,
				master,
				length,
				salt: null,
				info: System.Text.Encoding.UTF8.GetBytes(info));
	}
}
```

Three details that matter. The tag covers the version byte and key id as well as the initialization vector and ciphertext, so neither can be swapped. The key identifier is derived from the master key rather than being its index, so it stays stable across rotation and reveals nothing about the key. And `Decrypt` is called only after the tag verifies, which is what keeps CBC padding behavior out of reach of an attacker.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~HrefTokenCodecTests"`
Expected: PASS. The tamper test runs one assertion per byte of the token, so it exercises every region.

If `encryption.Decrypt` has a different overload set than assumed, check the real surface in `../Essentials/Essentials/IEncryptionProvider.cs` (it offers `Decrypt(ReadOnlySpan<byte>, ReadOnlySpan<byte>, ReadOnlySpan<byte>)` alongside `Try*` and stream variants) and adjust the call, not the wire format.

- [ ] **Step 7: Build clean and run the whole suite**

Run: `dotnet build --no-incremental && dotnet test`
Expected: zero warnings, all tests pass.

- [ ] **Step 8: Update the spec's encoding sentence**

The spec says the token is Base64url encoded "through Essentials' `Base64` provider". That provider emits standard base64, which is not URL-safe. Edit `docs/superpowers/specs/2026-08-18-gitlfscache-design.md`, in the token construction list, step 5, to read:

```markdown
5. Concatenate version byte, key identifier, initialization vector, ciphertext, and tag, then encode with `System.Buffers.Text.Base64Url`. Essentials' `Base64` provider is not used here because it emits standard base64, whose `+`, `/`, and `=` characters are not URL-safe.
```

- [ ] **Step 9: Commit**

```bash
git add GitLfsCache/Tokens GitLfsCache.Tests/Tokens Directory.Packages.props GitLfsCache.Tests/GitLfsCache.Tests.csproj docs/superpowers/specs
git commit -m "[minor] Add authenticated href token codec with key rotation"
```

---

## Task 5: Batch rewriter

**Files:**
- Create: `GitLfsCache/Batch/BatchRewriteContext.cs`, `GitLfsCache/Batch/BatchRewriter.cs`
- Create: `GitLfsCache.Tests/Batch/BatchRewriterTests.cs`
- Create: `GitLfsCache.Tests/TestData/github-download-batch.json`, `ado-download-batch.json`, `github-upload-batch.json`, `mixed-error-batch.json`

**Interfaces:**
- Consumes: `IHrefTokenCodec.Encode`, `HrefToken`, `TokenAction` from Task 4. `GitLfsCacheOptions.TokenLifetime` from Task 3.
- Produces:
  - `sealed record BatchRewriteContext` with `required string Upstream`, `required string RepositoryPath`, `required Uri PublicBaseUrl`.
  - `sealed class BatchRewriter(IHrefTokenCodec codec, IOptions<GitLfsCacheOptions> options, TimeProvider timeProvider)` with `public JsonNode Rewrite(JsonNode upstreamResponse, BatchRewriteContext context)`.

The transform works on `JsonNode` rather than typed models on purpose. A proxy that deserializes into records silently drops any property upstream sends that the records do not declare, and the Git LFS specification is extended in practice (`hash_algo`, `authenticated`, forge-specific fields). Mutating a node tree preserves everything untouched.

- [ ] **Step 1: Write the captured payload fixtures**

`GitLfsCache.Tests/TestData/github-download-batch.json`:

```json
{
  "transfer": "basic",
  "objects": [
    {
      "oid": "9a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8",
      "size": 12345,
      "authenticated": true,
      "actions": {
        "download": {
          "href": "https://objects.githubusercontent.com/storage/abc?sig=xyz",
          "expires_in": 3600
        }
      }
    }
  ],
  "hash_algo": "sha256"
}
```

`GitLfsCache.Tests/TestData/ado-download-batch.json`. Azure DevOps returns a credential in the action header, which is exactly what must never reach the client:

```json
{
  "transfer": "basic",
  "objects": [
    {
      "oid": "1111111111111111111111111111111111111111111111111111111111111111",
      "size": 500,
      "actions": {
        "download": {
          "href": "https://dev.azure.com/org/_apis/lfs/objects/1111",
          "header": {
            "Authorization": "Bearer ado-secret-token",
            "X-TFS-FedAuthRedirect": "Suppress"
          },
          "expires_at": "2026-08-18T13:00:00Z"
        }
      }
    }
  ]
}
```

`GitLfsCache.Tests/TestData/github-upload-batch.json`:

```json
{
  "transfer": "basic",
  "objects": [
    {
      "oid": "2222222222222222222222222222222222222222222222222222222222222222",
      "size": 900,
      "actions": {
        "upload": {
          "href": "https://objects.githubusercontent.com/upload/2222?sig=put",
          "header": { "Authorization": "Bearer upload-secret" }
        },
        "verify": {
          "href": "https://github.com/owner/repo.git/info/lfs/objects/verify",
          "header": { "Authorization": "Bearer verify-secret" }
        }
      }
    }
  ]
}
```

`GitLfsCache.Tests/TestData/mixed-error-batch.json`:

```json
{
  "transfer": "basic",
  "objects": [
    {
      "oid": "3333333333333333333333333333333333333333333333333333333333333333",
      "size": 10,
      "actions": {
        "download": { "href": "https://upstream.example/ok" }
      }
    },
    {
      "oid": "4444444444444444444444444444444444444444444444444444444444444444",
      "size": 20,
      "error": { "code": 404, "message": "Object does not exist" }
    }
  ]
}
```

Mark them as copied content in `GitLfsCache.Tests/GitLfsCache.Tests.csproj`:

```xml
<ItemGroup>
  <Content Include="TestData\**\*.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing tests**

`GitLfsCache.Tests/Batch/BatchRewriterTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Batch;

using System.Text.Json.Nodes;
using ktsu.Essentials.EncryptionProviders;
using ktsu.GitLfsCache.Batch;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class BatchRewriterTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	private static HrefTokenCodec Codec()
	{
		GitLfsCacheOptions options = new() { TokenLifetime = TimeSpan.FromHours(1) };
		options.TokenKeys.Add(Convert.ToBase64String(new byte[32]));
		return new HrefTokenCodec(new Aes(), Options.Create(options), new FakeTimeProvider(Now));
	}

	private static (BatchRewriter Rewriter, HrefTokenCodec Codec) Create()
	{
		GitLfsCacheOptions options = new() { TokenLifetime = TimeSpan.FromHours(1) };
		options.TokenKeys.Add(Convert.ToBase64String(new byte[32]));
		FakeTimeProvider time = new(Now);
		HrefTokenCodec codec = new(new Aes(), Options.Create(options), time);
		return (new BatchRewriter(codec, Options.Create(options), time), codec);
	}

	private static BatchRewriteContext Context() => new()
	{
		Upstream = "github",
		RepositoryPath = "owner/repo.git/info/lfs",
		PublicBaseUrl = new Uri("https://cache.example"),
	};

	private static JsonNode Load(string fileName) =>
		JsonNode.Parse(File.ReadAllText(Path.Combine("TestData", fileName)))!;

	private static JsonObject FirstAction(JsonNode rewritten, string action) =>
		rewritten["objects"]![0]!["actions"]![action]!.AsObject();

	[TestMethod]
	public void Rewrite_DownloadHref_PointsAtTheProxy()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-download-batch.json"), Context());
		string href = FirstAction(rewritten, "download")["href"]!.GetValue<string>();

		Assert.StartsWith(
			"https://cache.example/github/owner/repo.git/info/lfs/objects/9a1f2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d5e6f708192a3b4c5d6e7f8?t=",
			href);
	}

	[TestMethod]
	public void Rewrite_DownloadToken_CarriesTheUpstreamAction()
	{
		(BatchRewriter rewriter, HrefTokenCodec codec) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("ado-download-batch.json"), Context());
		string href = FirstAction(rewritten, "download")["href"]!.GetValue<string>();
		string encoded = new Uri(href).Query.Replace("?t=", string.Empty, StringComparison.Ordinal);

		Assert.IsTrue(codec.TryDecode(encoded, out HrefToken? token, out string? failure), failure);
		Assert.IsNotNull(token);
		Assert.AreEqual("https://dev.azure.com/org/_apis/lfs/objects/1111", token.UpstreamHref);
		Assert.AreEqual("Bearer ado-secret-token", token.UpstreamHeaders["Authorization"]);
		Assert.AreEqual("Suppress", token.UpstreamHeaders["X-TFS-FedAuthRedirect"]);
		Assert.AreEqual(TokenAction.Download, token.Action);
		Assert.AreEqual("github", token.Upstream);
		Assert.AreEqual(500L, token.Size);
	}

	[TestMethod]
	public void Rewrite_RemovesTheUpstreamCredentialFromTheResponse()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("ado-download-batch.json"), Context());

		Assert.IsFalse(FirstAction(rewritten, "download").ContainsKey("header"));
		Assert.DoesNotContain("ado-secret-token", rewritten.ToJsonString());
	}

	[TestMethod]
	public void Rewrite_SetsExpiresInFromTokenLifetimeAndDropsExpiresAt()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonObject action = FirstAction(rewriter.Rewrite(Load("ado-download-batch.json"), Context()), "download");

		Assert.AreEqual(3600, action["expires_in"]!.GetValue<int>());
		Assert.IsFalse(action.ContainsKey("expires_at"));
	}

	[TestMethod]
	public void Rewrite_PreservesUnknownProperties()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-download-batch.json"), Context());

		Assert.AreEqual("sha256", rewritten["hash_algo"]!.GetValue<string>());
		Assert.AreEqual("basic", rewritten["transfer"]!.GetValue<string>());
		Assert.IsTrue(rewritten["objects"]![0]!["authenticated"]!.GetValue<bool>());
		Assert.AreEqual(12345L, rewritten["objects"]![0]!["size"]!.GetValue<long>());
	}

	[TestMethod]
	public void Rewrite_UploadAndVerify_BothPointAtTheProxyWithDistinctRoutes()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-upload-batch.json"), Context());
		string upload = FirstAction(rewritten, "upload")["href"]!.GetValue<string>();
		string verify = FirstAction(rewritten, "verify")["href"]!.GetValue<string>();

		const string oid = "2222222222222222222222222222222222222222222222222222222222222222";
		Assert.StartsWith($"https://cache.example/github/owner/repo.git/info/lfs/objects/{oid}?t=", upload);
		Assert.StartsWith($"https://cache.example/github/owner/repo.git/info/lfs/objects/{oid}/verify?t=", verify);
	}

	[TestMethod]
	public void Rewrite_UploadAndVerifyTokens_CarryDistinctActions()
	{
		(BatchRewriter rewriter, HrefTokenCodec codec) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("github-upload-batch.json"), Context());

		Assert.AreEqual(TokenAction.Upload, DecodeAction(codec, FirstAction(rewritten, "upload")));
		Assert.AreEqual(TokenAction.Verify, DecodeAction(codec, FirstAction(rewritten, "verify")));

		static string DecodeAction(HrefTokenCodec codec, JsonObject action)
		{
			string encoded = new Uri(action["href"]!.GetValue<string>()).Query
				.Replace("?t=", string.Empty, StringComparison.Ordinal);
			Assert.IsTrue(codec.TryDecode(encoded, out HrefToken? token, out string? _));
			return token!.Action;
		}
	}

	[TestMethod]
	public void Rewrite_ObjectWithAnError_PassesThroughUntouched()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("mixed-error-batch.json"), Context());
		JsonNode failed = rewritten["objects"]![1]!;

		Assert.AreEqual(404, failed["error"]!["code"]!.GetValue<int>());
		Assert.AreEqual("Object does not exist", failed["error"]!["message"]!.GetValue<string>());
		Assert.IsFalse(failed.AsObject().ContainsKey("actions"));
	}

	[TestMethod]
	public void Rewrite_ObjectWithAnError_DoesNotStopLaterObjects()
	{
		(BatchRewriter rewriter, _) = Create();

		JsonNode rewritten = rewriter.Rewrite(Load("mixed-error-batch.json"), Context());

		Assert.StartsWith("https://cache.example/", FirstAction(rewritten, "download")["href"]!.GetValue<string>());
	}

	[TestMethod]
	public void Rewrite_ResponseWithNoObjectsArray_IsReturnedUnchanged()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse("""{"message":"Repository not found","documentation_url":"x"}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.AreEqual("Repository not found", rewritten["message"]!.GetValue<string>());
	}

	[TestMethod]
	public void Rewrite_ObjectWithEmptyActions_IsLeftAlone()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse("""{"objects":[{"oid":"aa","size":1,"actions":{}}]}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.HasCount(0, rewritten["objects"]![0]!["actions"]!.AsObject());
	}

	[TestMethod]
	public void Rewrite_UnknownActionName_IsLeftUntouched()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = JsonNode.Parse(
			"""{"objects":[{"oid":"aa","size":1,"actions":{"custom":{"href":"https://upstream.example/x"}}}]}""")!;

		JsonNode rewritten = rewriter.Rewrite(input, Context());

		Assert.AreEqual(
			"https://upstream.example/x",
			rewritten["objects"]![0]!["actions"]!["custom"]!["href"]!.GetValue<string>());
	}

	[TestMethod]
	public void Rewrite_DoesNotMutateTheInputNode()
	{
		(BatchRewriter rewriter, _) = Create();
		JsonNode input = Load("ado-download-batch.json");
		string before = input.ToJsonString();

		rewriter.Rewrite(input, Context());

		Assert.AreEqual(before, input.ToJsonString());
	}
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~BatchRewriterTests"`
Expected: FAIL, `BatchRewriter` does not exist.

- [ ] **Step 4: Implement `BatchRewriteContext`**

`GitLfsCache/Batch/BatchRewriteContext.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Batch;

/// <summary>
/// The request context a batch response is rewritten against.
/// </summary>
public sealed record BatchRewriteContext
{
	/// <summary>Gets the configured upstream key from the request path.</summary>
	public required string Upstream { get; init; }

	/// <summary>
	/// Gets the path between the upstream key and <c>/objects/batch</c>, for example
	/// <c>owner/repo.git/info/lfs</c>, with no leading or trailing slash.
	/// </summary>
	public required string RepositoryPath { get; init; }

	/// <summary>Gets the externally reachable base URL rewritten hrefs are built from.</summary>
	public required Uri PublicBaseUrl { get; init; }
}
```

- [ ] **Step 5: Implement `BatchRewriter`**

`GitLfsCache/Batch/BatchRewriter.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Batch;

using System.Text.Json.Nodes;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Tokens;
using Microsoft.Extensions.Options;

/// <summary>
/// Rewrites the transfer URLs in an upstream batch response to point at this proxy.
/// </summary>
/// <remarks>
/// The transform works on a node tree rather than typed models so that properties this proxy does
/// not know about survive. The Git LFS batch response is extended in practice, by the
/// specification itself (<c>hash_algo</c>, <c>authenticated</c>) and by individual forges, and a
/// proxy that silently drops what it cannot name is a proxy that breaks clients unpredictably.
/// </remarks>
/// <param name="codec">The token codec.</param>
/// <param name="options">The configured options, supplying the token lifetime.</param>
/// <param name="timeProvider">Clock, injected so token expiry is testable.</param>
public sealed class BatchRewriter(
	IHrefTokenCodec codec,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider)
{
	/// <summary>
	/// Rewrites a batch response, leaving the input untouched.
	/// </summary>
	/// <param name="upstreamResponse">The parsed upstream response.</param>
	/// <param name="context">The request context.</param>
	/// <returns>A new node tree with rewritten hrefs.</returns>
	public JsonNode Rewrite(JsonNode upstreamResponse, BatchRewriteContext context)
	{
		Ensure.NotNull(upstreamResponse);
		Ensure.NotNull(context);

		// Deep copy so callers keep an unmodified original, which the relay path relies on when
		// it decides to pass a response through instead.
		JsonNode rewritten = upstreamResponse.DeepClone();

		if (rewritten is not JsonObject root
			|| !root.TryGetPropertyValue("objects", out JsonNode? objectsNode)
			|| objectsNode is not JsonArray objects)
		{
			return rewritten;
		}

		TimeSpan lifetime = options.Value.TokenLifetime;
		DateTimeOffset expiresAt = timeProvider.GetUtcNow().Add(lifetime);

		foreach (JsonNode? entry in objects)
		{
			if (entry is not JsonObject batchObject)
			{
				continue;
			}

			RewriteObject(batchObject, context, expiresAt, (int)lifetime.TotalSeconds);
		}

		return rewritten;
	}

	private void RewriteObject(
		JsonObject batchObject,
		BatchRewriteContext context,
		DateTimeOffset expiresAt,
		int expiresInSeconds)
	{
		if (!batchObject.TryGetPropertyValue("actions", out JsonNode? actionsNode)
			|| actionsNode is not JsonObject actions)
		{
			// An object upstream reported an error for, or one already present upstream with no
			// transfer required. Either way there is nothing to rewrite.
			return;
		}

		string? oid = batchObject["oid"]?.GetValue<string>();

		if (string.IsNullOrEmpty(oid))
		{
			return;
		}

		long size = batchObject["size"]?.GetValue<long>() ?? 0;

		foreach (string actionName in new[] { TokenAction.Download, TokenAction.Upload, TokenAction.Verify })
		{
			if (actions.TryGetPropertyValue(actionName, out JsonNode? actionNode)
				&& actionNode is JsonObject action)
			{
				RewriteAction(action, actionName, oid, size, context, expiresAt, expiresInSeconds);
			}
		}
	}

	private void RewriteAction(
		JsonObject action,
		string actionName,
		string oid,
		long size,
		BatchRewriteContext context,
		DateTimeOffset expiresAt,
		int expiresInSeconds)
	{
		string? upstreamHref = action["href"]?.GetValue<string>();

		if (string.IsNullOrEmpty(upstreamHref))
		{
			return;
		}

		Dictionary<string, string> headers = [];

		if (action["header"] is JsonObject headerObject)
		{
			foreach ((string name, JsonNode? value) in headerObject)
			{
				if (value is not null)
				{
					headers[name] = value.GetValue<string>();
				}
			}
		}

		string token = codec.Encode(new HrefToken
		{
			Oid = oid,
			Size = size,
			Upstream = context.Upstream,
			Action = actionName,
			UpstreamHref = upstreamHref,
			UpstreamHeaders = headers,
			ExpiresAt = expiresAt,
		});

		action["href"] = BuildProxyHref(actionName, oid, token, context);

		// The credential now lives inside the token. Leaving it here would hand every client the
		// upstream's bearer token, which is the whole point of terminating the transfer locally.
		action.Remove("header");

		// expires_at would contradict the proxy's own token lifetime, so it is replaced rather
		// than left to disagree.
		action.Remove("expires_at");
		action["expires_in"] = expiresInSeconds;
	}

	private static string BuildProxyHref(
		string actionName,
		string oid,
		string token,
		BatchRewriteContext context)
	{
		string suffix = actionName == TokenAction.Verify ? "/verify" : string.Empty;
		string basePath = context.PublicBaseUrl.ToString().TrimEnd('/');

		return $"{basePath}/{context.Upstream}/{context.RepositoryPath}/objects/{oid}{suffix}?t={token}";
	}
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~BatchRewriterTests"`
Expected: PASS.

- [ ] **Step 7: Build clean and run the whole suite**

Run: `dotnet build --no-incremental && dotnet test`
Expected: zero warnings, all tests pass.

- [ ] **Step 8: Commit**

```bash
git add GitLfsCache/Batch GitLfsCache.Tests/Batch GitLfsCache.Tests/TestData GitLfsCache.Tests/GitLfsCache.Tests.csproj
git commit -m "[minor] Add batch response rewriter preserving unknown properties"
```

---

## Task 6: Tee stream

**Files:**
- Create: `GitLfsCache/Storage/TeeStream.cs`
- Create: `GitLfsCache.Tests/Storage/TeeStreamTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `sealed class TeeStream` with

```csharp
public static Task<long> CopyAsync(
	Stream source,
	Stream primary,
	Stream? secondary,
	Action<Exception>? onSecondaryFailure,
	CancellationToken cancellationToken)
```

Returns the number of bytes copied to `primary`. A `secondary` write failure is reported through `onSecondaryFailure`, the secondary is abandoned, and copying to `primary` continues. A `primary` failure propagates, because the client not receiving its bytes is a real failure.

- [ ] **Step 1: Write the failing tests**

`GitLfsCache.Tests/Storage/TeeStreamTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Storage;

using ktsu.GitLfsCache.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class TeeStreamTests
{
	private static byte[] Payload(int length)
	{
		byte[] payload = new byte[length];

		for (int index = 0; index < length; index++)
		{
			payload[index] = (byte)(index % 251);
		}

		return payload;
	}

	[TestMethod]
	public async Task CopyAsync_WritesTheSameBytesToBothSinks()
	{
		byte[] payload = Payload(300_000);
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();
		using MemoryStream secondary = new();

		long copied = await TeeStream.CopyAsync(source, primary, secondary, null, TestContext.CancellationTokenSource.Token);

		Assert.AreEqual(payload.Length, copied);
		CollectionAssert.AreEqual(payload, primary.ToArray());
		CollectionAssert.AreEqual(payload, secondary.ToArray());
	}

	[TestMethod]
	public async Task CopyAsync_NoSecondary_StillCopiesToPrimary()
	{
		byte[] payload = Payload(1024);
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();

		long copied = await TeeStream.CopyAsync(source, primary, null, null, TestContext.CancellationTokenSource.Token);

		Assert.AreEqual(payload.Length, copied);
		CollectionAssert.AreEqual(payload, primary.ToArray());
	}

	[TestMethod]
	public async Task CopyAsync_EmptySource_CopiesNothingAndSucceeds()
	{
		using MemoryStream source = new([]);
		using MemoryStream primary = new();
		using MemoryStream secondary = new();

		long copied = await TeeStream.CopyAsync(source, primary, secondary, null, TestContext.CancellationTokenSource.Token);

		Assert.AreEqual(0L, copied);
		Assert.AreEqual(0, primary.Length);
	}

	[TestMethod]
	public async Task CopyAsync_SecondaryThrows_PrimaryStillReceivesEverything()
	{
		byte[] payload = Payload(300_000);
		using MemoryStream source = new(payload);
		using MemoryStream primary = new();
		using ThrowingStream secondary = new(failAfterBytes: 1024);
		List<Exception> failures = [];

		long copied = await TeeStream.CopyAsync(
			source, primary, secondary, failures.Add, TestContext.CancellationTokenSource.Token);

		Assert.AreEqual(payload.Length, copied);
		CollectionAssert.AreEqual(payload, primary.ToArray());
		Assert.HasCount(1, failures);
		Assert.IsInstanceOfType<IOException>(failures[0]);
	}

	[TestMethod]
	public async Task CopyAsync_PrimaryThrows_Propagates()
	{
		using MemoryStream source = new(Payload(300_000));
		using ThrowingStream primary = new(failAfterBytes: 1024);
		using MemoryStream secondary = new();

		await Assert.ThrowsExactlyAsync<IOException>(async () =>
			await TeeStream.CopyAsync(source, primary, secondary, null, TestContext.CancellationTokenSource.Token));
	}

	[TestMethod]
	public async Task CopyAsync_Cancelled_ThrowsOperationCancelled()
	{
		using MemoryStream source = new(Payload(300_000));
		using MemoryStream primary = new();
		using CancellationTokenSource cancellation = new();
		await cancellation.CancelAsync();

		await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
			await TeeStream.CopyAsync(source, primary, null, null, cancellation.Token));
	}

	/// <summary>Test double that accepts a fixed number of bytes and then fails every write.</summary>
	private sealed class ThrowingStream(int failAfterBytes) : Stream
	{
		private long _written;

		public override bool CanRead => false;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => _written;

		public override long Position
		{
			get => _written;
			set => throw new NotSupportedException();
		}

		public override void Flush()
		{
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) =>
			WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

		public override ValueTask WriteAsync(
			ReadOnlyMemory<byte> buffer,
			CancellationToken cancellationToken = default)
		{
			if (_written >= failAfterBytes)
			{
				throw new IOException("Simulated sink failure.");
			}

			_written += buffer.Length;
			return ValueTask.CompletedTask;
		}
	}

	/// <summary>Gets or sets the MSTest-supplied test context.</summary>
	public TestContext TestContext { get; set; } = null!;
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TeeStreamTests"`
Expected: FAIL, `TeeStream` does not exist.

- [ ] **Step 3: Implement `TeeStream`**

`GitLfsCache/Storage/TeeStream.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.Buffers;

/// <summary>
/// Copies one source stream to two sinks, where the second sink is allowed to fail.
/// </summary>
/// <remarks>
/// This is how an object is cached on the way through: the client is the primary sink and the
/// staging file is the secondary. The asymmetry is deliberate. A client not receiving its bytes is
/// a failed request, while a store write that fails only means the cache stays cold, so the
/// secondary is abandoned and the transfer continues.
///
/// Nothing is buffered whole, so memory use is flat regardless of object size.
/// </remarks>
public static class TeeStream
{
	private const int BufferSize = 81_920;

	/// <summary>
	/// Copies <paramref name="source"/> to both sinks.
	/// </summary>
	/// <param name="source">The stream to read.</param>
	/// <param name="primary">The sink whose failures propagate.</param>
	/// <param name="secondary">The sink whose failures are reported and then ignored, or null.</param>
	/// <param name="onSecondaryFailure">Invoked once if the secondary sink fails.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The number of bytes written to <paramref name="primary"/>.</returns>
	public static async Task<long> CopyAsync(
		Stream source,
		Stream primary,
		Stream? secondary,
		Action<Exception>? onSecondaryFailure,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(source);
		Ensure.NotNull(primary);

		byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
		Stream? liveSecondary = secondary;
		long total = 0;

		try
		{
			while (true)
			{
				int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken)
					.ConfigureAwait(false);

				if (read == 0)
				{
					break;
				}

				await primary.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
				total += read;

				if (liveSecondary is not null)
				{
					try
					{
						await liveSecondary.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
							.ConfigureAwait(false);
					}
					catch (Exception failure) when (failure is IOException or ObjectDisposedException
						or UnauthorizedAccessException or NotSupportedException)
					{
						liveSecondary = null;
						onSecondaryFailure?.Invoke(failure);
					}
				}
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}

		return total;
	}
}
```

The `when` filter is narrow on purpose. Swallowing every exception from the secondary would hide programming errors, and a cancellation must never be mistaken for a store failure.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TeeStreamTests"`
Expected: PASS.

- [ ] **Step 5: Build clean and run the whole suite**

Run: `dotnet build --no-incremental && dotnet test`
Expected: zero warnings, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add GitLfsCache/Storage/TeeStream.cs GitLfsCache.Tests/Storage/TeeStreamTests.cs
git commit -m "[minor] Add tee stream copying to a client and a fail-soft store sink"
```

---

## Task 7: Object store

**Files:**
- Create: `GitLfsCache/Storage/StoredObject.cs`, `StagedFile.cs`, `StagingHandle.cs`, `IObjectStore.cs`, `ObjectStore.cs`
- Create: `GitLfsCache.Tests/Storage/ObjectStoreTests.cs`

**Interfaces:**
- Consumes: `StoreOptions.Root`, `StoreOptions.StagingMaxAge` from Task 3.
- Produces:
  - `sealed record StoredObject(AbsoluteFilePath Path, string Upstream, string Oid, long Size, DateTimeOffset LastAccessUtc)`
  - `sealed record StagedFile(AbsoluteFilePath Path, DateTimeOffset CreatedUtc)`
  - `sealed class StagingHandle : IAsyncDisposable` exposing `Stream Stream { get; }` and `AbsoluteFilePath Path { get; }`. Disposing without publishing deletes the file.
  - `IObjectStore` with `bool TryOpenRead(string upstream, string oid, out Stream? stream, out long length)`, `StagingHandle OpenStaging(string upstream)`, `Task<bool> PublishAsync(StagingHandle handle, string upstream, string oid, CancellationToken cancellationToken)`, `void Touch(string upstream, string oid)`, `bool Exists(string upstream, string oid)`, `IEnumerable<StoredObject> Enumerate()`, `IEnumerable<StagedFile> EnumerateStaging()`, `bool TryDelete(StoredObject storedObject)`, `bool TryDeleteStaging(StagedFile staged)`, `long TotalBytes { get; }`, `void RecomputeTotalBytes()`.
  - `ObjectStore(IFileSystemProvider fileSystem, IHashProvider hashProvider, IOptions<GitLfsCacheOptions> options, TimeProvider timeProvider, ILogger<ObjectStore> logger)`.

Path handling uses `.As<T>()` on strings and relies on the implicit `SemanticString` to `string` conversion when calling filesystem APIs, rather than `Create` factories or `WeakString`.

- [ ] **Step 1: Write the failing tests**

`GitLfsCache.Tests/Storage/ObjectStoreTests.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Storage;

using System.Security.Cryptography;
using System.Text;
using ktsu.Essentials;
using ktsu.Essentials.HashProviders;
using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testably.Abstractions.Testing;

[TestClass]
public class ObjectStoreTests
{
	private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

	private const string Root = "/var/lib/gitlfscache";

	private static (ObjectStore Store, MockFileSystem FileSystem, FakeTimeProvider Time) Create()
	{
		MockFileSystem fileSystem = new();
		fileSystem.Directory.CreateDirectory(Root);

		GitLfsCacheOptions options = new()
		{
			Store = new StoreOptions { Root = Root, MaxSize = "1GB", LowWaterMark = 0.9 },
		};

		FakeTimeProvider time = new(Now);
		TestFileSystemProvider provider = new(fileSystem);

		ObjectStore store = new(
			provider,
			new SHA256(),
			Options.Create(options),
			time,
			NullLogger<ObjectStore>.Instance);

		return (store, fileSystem, time);
	}

	private static (byte[] Content, string Oid) Content(string text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text);
		return (bytes, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
	}

	private static async Task<string> StoreAsync(ObjectStore store, string upstream, string text)
	{
		(byte[] content, string oid) = Content(text);

		StagingHandle handle = store.OpenStaging(upstream);
		await handle.Stream.WriteAsync(content, TestContext.CancellationTokenSource.Token);
		bool published = await store.PublishAsync(handle, upstream, oid, TestContext.CancellationTokenSource.Token);

		Assert.IsTrue(published, "Publishing a correctly hashed object should succeed.");
		return oid;
	}

	[TestMethod]
	public async Task PublishAsync_CorrectHash_MakesTheObjectReadable()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("hello lfs");

		string oid = await StoreAsync(store, "github", "hello lfs");

		Assert.IsTrue(store.TryOpenRead("github", oid, out Stream? stream, out long length));
		Assert.IsNotNull(stream);
		Assert.AreEqual(content.Length, length);

		await using (stream)
		{
			using MemoryStream read = new();
			await stream.CopyToAsync(read, TestContext.CancellationTokenSource.Token);
			CollectionAssert.AreEqual(content, read.ToArray());
		}
	}

	[TestMethod]
	public async Task PublishAsync_UsesTwoLevelFanOutUnderTheUpstreamKey()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();

		string oid = await StoreAsync(store, "github", "fan out");

		string expected = fileSystem.Path.Combine(
			Root, "github", "objects", oid[..2], oid[2..4], oid);

		Assert.IsTrue(fileSystem.File.Exists(expected), $"Expected the object at {expected}.");
	}

	[TestMethod]
	public async Task PublishAsync_HashMismatch_DiscardsStagingAndReturnsFalse()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();
		const string wrongOid = "0000000000000000000000000000000000000000000000000000000000000000";

		StagingHandle handle = store.OpenStaging("github");
		await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("not what the oid claims"), TestContext.CancellationTokenSource.Token);

		bool published = await store.PublishAsync(handle, "github", wrongOid, TestContext.CancellationTokenSource.Token);

		Assert.IsFalse(published);
		Assert.IsFalse(store.Exists("github", wrongOid));
		Assert.IsFalse(fileSystem.File.Exists(handle.Path), "Staging must not survive a mismatch.");
	}

	[TestMethod]
	public async Task PublishAsync_ObjectAlreadyPresent_SucceedsAndRemovesStaging()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();
		string oid = await StoreAsync(store, "github", "duplicate");
		(byte[] content, _) = Content("duplicate");

		StagingHandle second = store.OpenStaging("github");
		await second.Stream.WriteAsync(content, TestContext.CancellationTokenSource.Token);

		bool published = await store.PublishAsync(second, "github", oid, TestContext.CancellationTokenSource.Token);

		Assert.IsTrue(published);
		Assert.IsFalse(fileSystem.File.Exists(second.Path));
	}

	[TestMethod]
	public async Task StagingHandle_DisposedWithoutPublishing_DeletesTheFile()
	{
		(ObjectStore store, MockFileSystem fileSystem, _) = Create();

		StagingHandle handle = store.OpenStaging("github");
		string path = handle.Path;
		await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("abandoned"), TestContext.CancellationTokenSource.Token);
		await handle.DisposeAsync();

		Assert.IsFalse(fileSystem.File.Exists(path));
	}

	[TestMethod]
	public void TryOpenRead_MissingObject_ReturnsFalse()
	{
		(ObjectStore store, _, _) = Create();

		Assert.IsFalse(store.TryOpenRead("github", new string('a', 64), out Stream? stream, out long length));
		Assert.IsNull(stream);
		Assert.AreEqual(0L, length);
	}

	[TestMethod]
	public async Task Upstreams_AreNamespacedSeparately()
	{
		(ObjectStore store, _, _) = Create();

		string oid = await StoreAsync(store, "github", "shared bytes");

		Assert.IsTrue(store.Exists("github", oid));
		Assert.IsFalse(store.Exists("ado", oid), "Object trees must not be shared between upstreams.");
	}

	[TestMethod]
	[DataRow("../../etc/passwd")]
	[DataRow("abc")]
	[DataRow("")]
	[DataRow("ZZZZ111111111111111111111111111111111111111111111111111111111111")]
	[DataRow("9A1F2B3C4D5E6F708192A3B4C5D6E7F8091A2B3C4D5E6F708192A3B4C5D6E7F8")]
	public void TryOpenRead_MalformedOid_ReturnsFalseWithoutTouchingTheFilesystem(string oid)
	{
		(ObjectStore store, _, _) = Create();

		Assert.IsFalse(store.TryOpenRead("github", oid, out Stream? _, out long _));
	}

	[TestMethod]
	[DataRow("../escape")]
	[DataRow("with/slash")]
	[DataRow("")]
	public void OpenStaging_MalformedUpstream_Throws(string upstream)
	{
		(ObjectStore store, _, _) = Create();

		Assert.ThrowsExactly<ArgumentException>(() => store.OpenStaging(upstream));
	}

	[TestMethod]
	public async Task Touch_UpdatesLastAccessTime()
	{
		(ObjectStore store, _, FakeTimeProvider time) = Create();
		string oid = await StoreAsync(store, "github", "touch me");

		time.Advance(TimeSpan.FromHours(3));
		store.Touch("github", oid);

		StoredObject stored = store.Enumerate().Single();
		Assert.AreEqual(Now.AddHours(3).UtcDateTime, stored.LastAccessUtc.UtcDateTime);
	}

	[TestMethod]
	public void Touch_MissingObject_DoesNotThrow()
	{
		(ObjectStore store, _, _) = Create();

		store.Touch("github", new string('b', 64));
	}

	[TestMethod]
	public async Task Enumerate_ReturnsEveryStoredObjectAcrossUpstreams()
	{
		(ObjectStore store, _, _) = Create();
		await StoreAsync(store, "github", "one");
		await StoreAsync(store, "github", "two");
		await StoreAsync(store, "ado", "three");

		Assert.HasCount(3, store.Enumerate().ToList());
	}

	[TestMethod]
	public async Task Enumerate_ReportsSizeUpstreamAndOid()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("measured");
		string oid = await StoreAsync(store, "github", "measured");

		StoredObject stored = store.Enumerate().Single();

		Assert.AreEqual(oid, stored.Oid);
		Assert.AreEqual("github", stored.Upstream);
		Assert.AreEqual(content.Length, stored.Size);
	}

	[TestMethod]
	public async Task TotalBytes_TracksPublishAndDelete()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("accounted for");

		await StoreAsync(store, "github", "accounted for");
		Assert.AreEqual(content.Length, store.TotalBytes);

		Assert.IsTrue(store.TryDelete(store.Enumerate().Single()));
		Assert.AreEqual(0L, store.TotalBytes);
	}

	[TestMethod]
	public async Task RecomputeTotalBytes_RebuildsTheCounterFromDisk()
	{
		(ObjectStore store, _, _) = Create();
		(byte[] content, _) = Content("rebuilt");
		await StoreAsync(store, "github", "rebuilt");

		store.RecomputeTotalBytes();

		Assert.AreEqual(content.Length, store.TotalBytes);
	}

	[TestMethod]
	public async Task EnumerateStaging_ReportsOrphanedFilesWithCreationTime()
	{
		(ObjectStore store, _, FakeTimeProvider time) = Create();
		StagingHandle handle = store.OpenStaging("github");
		await handle.Stream.WriteAsync(Encoding.UTF8.GetBytes("orphan"), TestContext.CancellationTokenSource.Token);
		await handle.Stream.FlushAsync(TestContext.CancellationTokenSource.Token);

		StagedFile staged = store.EnumerateStaging().Single();

		Assert.AreEqual(Now.UtcDateTime, staged.CreatedUtc.UtcDateTime);
		Assert.IsTrue(store.TryDeleteStaging(staged));
		Assert.HasCount(0, store.EnumerateStaging().ToList());
	}

	/// <summary>Adapts a mock filesystem to the Essentials provider interface.</summary>
	private sealed class TestFileSystemProvider(MockFileSystem inner) : IFileSystemProvider
	{
		public IDirectory Directory => inner.Directory;

		public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;

		public IDriveInfoFactory DriveInfo => inner.DriveInfo;

		public IFile File => inner.File;

		public IFileInfoFactory FileInfo => inner.FileInfo;

		public IFileStreamFactory FileStream => inner.FileStream;

		public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;

		public IPath Path => inner.Path;
	}

	/// <summary>Gets or sets the MSTest-supplied test context.</summary>
	public static TestContext TestContext { get; set; } = null!;
}
```

`TestFileSystemProvider` exists because `IFileSystemProvider` is a marker interface over `System.IO.Abstractions.IFileSystem` and `MockFileSystem` does not implement the ktsu marker. If the interface's member list differs from the above, copy it from `../Essentials/Essentials/IFileSystemProvider.cs` and the `IFileSystem` interface it extends.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ObjectStoreTests"`
Expected: FAIL, `ObjectStore` does not exist.

- [ ] **Step 3: Implement the record types and staging handle**

`GitLfsCache/Storage/StoredObject.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using ktsu.Semantics.Paths;

/// <summary>
/// One published object in the store.
/// </summary>
/// <param name="Path">The absolute path of the object file.</param>
/// <param name="Upstream">The upstream key whose tree holds it.</param>
/// <param name="Oid">The object id, a lowercase hex SHA256 digest.</param>
/// <param name="Size">The object size in bytes.</param>
/// <param name="LastAccessUtc">When the object was last served, used to order eviction.</param>
public sealed record StoredObject(
	AbsoluteFilePath Path,
	string Upstream,
	string Oid,
	long Size,
	DateTimeOffset LastAccessUtc);
```

`GitLfsCache/Storage/StagedFile.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using ktsu.Semantics.Paths;

/// <summary>
/// An in-progress or orphaned staging file.
/// </summary>
/// <param name="Path">The absolute path of the staging file.</param>
/// <param name="CreatedUtc">When the staging file was created, used to age out orphans.</param>
public sealed record StagedFile(AbsoluteFilePath Path, DateTimeOffset CreatedUtc);
```

`GitLfsCache/Storage/StagingHandle.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.IO.Abstractions;
using ktsu.Semantics.Paths;

/// <summary>
/// A staging file being written before it is verified and published.
/// </summary>
/// <remarks>
/// Disposing without a successful publish deletes the file. That is what keeps a cancelled or
/// failed transfer from leaving a partial object behind, and it means the caller does not have to
/// remember to clean up on every failure path.
/// </remarks>
public sealed class StagingHandle : IAsyncDisposable
{
	private readonly IFileSystem _fileSystem;
	private bool _published;
	private bool _disposed;

	internal StagingHandle(IFileSystem fileSystem, AbsoluteFilePath path, Stream stream)
	{
		_fileSystem = fileSystem;
		Path = path;
		Stream = stream;
	}

	/// <summary>Gets the absolute path of the staging file.</summary>
	public AbsoluteFilePath Path { get; }

	/// <summary>Gets the writable stream for the staging file.</summary>
	public Stream Stream { get; }

	/// <summary>Marks the staging file as published so disposal leaves it alone.</summary>
	internal void MarkPublished() => _published = true;

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		await Stream.DisposeAsync().ConfigureAwait(false);

		if (_published)
		{
			return;
		}

		try
		{
			if (_fileSystem.File.Exists(Path))
			{
				_fileSystem.File.Delete(Path);
			}
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// Age-based staging cleanup will collect it. Throwing from disposal would replace a
			// harmless leftover file with a failed request.
		}
	}
}
```

`GitLfsCache/Storage/IObjectStore.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

/// <summary>
/// A content-addressed store of Git LFS objects, namespaced per upstream.
/// </summary>
public interface IObjectStore
{
	/// <summary>Gets the total size of every published object, in bytes.</summary>
	public long TotalBytes { get; }

	/// <summary>
	/// Opens a published object for reading.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	/// <param name="stream">The readable stream, or null when the object is absent.</param>
	/// <param name="length">The object length in bytes, or zero when absent.</param>
	/// <returns><see langword="true"/> when the object was opened.</returns>
	public bool TryOpenRead(string upstream, string oid, out Stream? stream, out long length);

	/// <summary>
	/// Creates a staging file to write an object into.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <returns>A handle that deletes the file unless it is published.</returns>
	/// <exception cref="ArgumentException"><paramref name="upstream"/> is not a valid key.</exception>
	public StagingHandle OpenStaging(string upstream);

	/// <summary>
	/// Verifies a staging file against an object id and publishes it on a match.
	/// </summary>
	/// <param name="handle">The staging handle, consumed by this call.</param>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The expected object id.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns><see langword="true"/> when the content hashed to <paramref name="oid"/>.</returns>
	public Task<bool> PublishAsync(
		StagingHandle handle,
		string upstream,
		string oid,
		CancellationToken cancellationToken);

	/// <summary>Records that an object was served, so eviction sees it as warm.</summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	public void Touch(string upstream, string oid);

	/// <summary>Reports whether an object is published.</summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="oid">The object id.</param>
	/// <returns><see langword="true"/> when the object is present.</returns>
	public bool Exists(string upstream, string oid);

	/// <summary>Enumerates every published object across every upstream.</summary>
	/// <returns>The published objects.</returns>
	public IEnumerable<StoredObject> Enumerate();

	/// <summary>Enumerates staging files across every upstream.</summary>
	/// <returns>The staging files.</returns>
	public IEnumerable<StagedFile> EnumerateStaging();

	/// <summary>Deletes a published object, reporting rather than throwing on failure.</summary>
	/// <param name="storedObject">The object to delete.</param>
	/// <returns><see langword="true"/> when the object was deleted.</returns>
	public bool TryDelete(StoredObject storedObject);

	/// <summary>Deletes a staging file, reporting rather than throwing on failure.</summary>
	/// <param name="staged">The staging file to delete.</param>
	/// <returns><see langword="true"/> when the file was deleted.</returns>
	public bool TryDeleteStaging(StagedFile staged);

	/// <summary>Rebuilds the byte counter by scanning the store.</summary>
	public void RecomputeTotalBytes();
}
```

- [ ] **Step 4: Implement `ObjectStore`**

`GitLfsCache/Storage/ObjectStore.cs`:

```csharp
// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Storage;

using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using ktsu.Essentials;
using ktsu.GitLfsCache.Configuration;
using ktsu.Semantics.Paths;
using ktsu.Semantics.Strings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Content-addressed object store over an abstracted filesystem.
/// </summary>
/// <remarks>
/// Layout is <c>{root}/{upstream}/objects/{oid[0..2]}/{oid[2..4]}/{oid}</c> with staging under
/// <c>{root}/{upstream}/staging</c>. Staging shares the volume with the objects so publishing is an
/// atomic rename, and the two-level fan-out mirrors the git-lfs client's own layout, which keeps
/// directory sizes reasonable into the hundreds of thousands of objects.
///
/// Access times are set explicitly rather than read from the filesystem, because <c>noatime</c> and
/// <c>relatime</c> mounts make filesystem access times unreliable and eviction depends on them.
/// </remarks>
/// <param name="fileSystem">The filesystem to store objects on.</param>
/// <param name="hashProvider">SHA256, used to verify an object before publishing it.</param>
/// <param name="options">The configured options.</param>
/// <param name="timeProvider">Clock, injected so access times are testable.</param>
/// <param name="logger">Logger.</param>
public sealed class ObjectStore(
	IFileSystemProvider fileSystem,
	IHashProvider hashProvider,
	IOptions<GitLfsCacheOptions> options,
	TimeProvider timeProvider,
	ILogger<ObjectStore> logger) : IObjectStore
{
	private const string ObjectsDirectoryName = "objects";
	private const string StagingDirectoryName = "staging";
	private const int OidLength = 64;

	private readonly AbsoluteDirectoryPath _root = options.Value.Store.Root.As<AbsoluteDirectoryPath>();
	private long _totalBytes;

	/// <inheritdoc />
	public long TotalBytes => Interlocked.Read(ref _totalBytes);

	/// <inheritdoc />
	public bool TryOpenRead(string upstream, string oid, out Stream? stream, out long length)
	{
		stream = null;
		length = 0;

		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			return false;
		}

		AbsoluteFilePath path = ObjectPath(upstream, oid);

		try
		{
			if (!fileSystem.File.Exists(path))
			{
				return false;
			}

			length = fileSystem.FileInfo.New(path).Length;

			// FileShare.Delete lets an eviction sweep remove this file while it is being served,
			// which Windows otherwise refuses.
			stream = fileSystem.FileStream.New(
				path,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);

			return true;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning(failure, "Could not open cached object {Oid} for {Upstream}.", oid, upstream);
			stream?.Dispose();
			stream = null;
			length = 0;
			return false;
		}
	}

	/// <inheritdoc />
	public StagingHandle OpenStaging(string upstream)
	{
		if (!IsValidUpstream(upstream))
		{
			throw new ArgumentException($"'{upstream}' is not a valid upstream key.", nameof(upstream));
		}

		AbsoluteDirectoryPath directory = StagingDirectory(upstream);
		fileSystem.Directory.CreateDirectory(directory);

		AbsoluteFilePath path = fileSystem.Path
			.Combine(directory, $"{Guid.NewGuid():N}.tmp")
			.As<AbsoluteFilePath>();

		Stream stream = fileSystem.FileStream.New(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

		return new StagingHandle(fileSystem, path, stream);
	}

	/// <inheritdoc />
	public async Task<bool> PublishAsync(
		StagingHandle handle,
		string upstream,
		string oid,
		CancellationToken cancellationToken)
	{
		Ensure.NotNull(handle);

		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}

		// Flush and release the write handle before hashing, so the digest covers every byte and
		// the rename is not blocked by an open writer.
		await handle.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
		await handle.Stream.DisposeAsync().ConfigureAwait(false);

		if (!await MatchesAsync(handle.Path, oid, cancellationToken).ConfigureAwait(false))
		{
			logger.LogWarning(
				"Discarding fetched object for {Upstream}: content did not hash to {Oid}.", upstream, oid);
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}

		AbsoluteFilePath destination = ObjectPath(upstream, oid);

		try
		{
			fileSystem.Directory.CreateDirectory(fileSystem.Path.GetDirectoryName(destination)!);

			if (fileSystem.File.Exists(destination))
			{
				// Another request published the same content first. Content addressing makes the
				// two byte-identical, so the winner stands and this copy is dropped.
				await handle.DisposeAsync().ConfigureAwait(false);
				return true;
			}

			long size = fileSystem.FileInfo.New(handle.Path).Length;
			fileSystem.File.Move(handle.Path, destination);
			handle.MarkPublished();
			Touch(upstream, oid);
			Interlocked.Add(ref _totalBytes, size);
			return true;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			logger.LogWarning(failure, "Could not publish object {Oid} for {Upstream}.", oid, upstream);
			await handle.DisposeAsync().ConfigureAwait(false);
			return false;
		}
	}

	/// <inheritdoc />
	public void Touch(string upstream, string oid)
	{
		if (!IsValidUpstream(upstream) || !IsValidOid(oid))
		{
			return;
		}

		try
		{
			AbsoluteFilePath path = ObjectPath(upstream, oid);

			if (fileSystem.File.Exists(path))
			{
				fileSystem.File.SetLastAccessTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
			}
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// A missed access time only makes this object look colder than it is, so it is not
			// worth failing a served request over.
			logger.LogDebug(failure, "Could not update access time for {Oid}.", oid);
		}
	}

	/// <inheritdoc />
	public bool Exists(string upstream, string oid) =>
		IsValidUpstream(upstream) && IsValidOid(oid) && fileSystem.File.Exists(ObjectPath(upstream, oid));

	/// <inheritdoc />
	public IEnumerable<StoredObject> Enumerate()
	{
		if (!fileSystem.Directory.Exists(_root))
		{
			yield break;
		}

		foreach (string upstreamDirectory in fileSystem.Directory.EnumerateDirectories(_root))
		{
			string upstream = fileSystem.Path.GetFileName(upstreamDirectory);
			string objectsDirectory = fileSystem.Path.Combine(upstreamDirectory, ObjectsDirectoryName);

			if (!fileSystem.Directory.Exists(objectsDirectory))
			{
				continue;
			}

			foreach (string file in fileSystem.Directory.EnumerateFiles(
				objectsDirectory, "*", SearchOption.AllDirectories))
			{
				IFileInfo info = fileSystem.FileInfo.New(file);
				string oid = fileSystem.Path.GetFileName(file);

				if (!IsValidOid(oid))
				{
					continue;
				}

				yield return new StoredObject(
					file.As<AbsoluteFilePath>(),
					upstream,
					oid,
					info.Length,
					new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero));
			}
		}
	}

	/// <inheritdoc />
	public IEnumerable<StagedFile> EnumerateStaging()
	{
		if (!fileSystem.Directory.Exists(_root))
		{
			yield break;
		}

		foreach (string upstreamDirectory in fileSystem.Directory.EnumerateDirectories(_root))
		{
			string stagingDirectory = fileSystem.Path.Combine(upstreamDirectory, StagingDirectoryName);

			if (!fileSystem.Directory.Exists(stagingDirectory))
			{
				continue;
			}

			foreach (string file in fileSystem.Directory.EnumerateFiles(stagingDirectory, "*.tmp"))
			{
				IFileInfo info = fileSystem.FileInfo.New(file);

				yield return new StagedFile(
					file.As<AbsoluteFilePath>(),
					new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero));
			}
		}
	}

	/// <inheritdoc />
	public bool TryDelete(StoredObject storedObject)
	{
		Ensure.NotNull(storedObject);

		if (TryDeleteFile(storedObject.Path))
		{
			Interlocked.Add(ref _totalBytes, -storedObject.Size);
			return true;
		}

		return false;
	}

	/// <inheritdoc />
	public bool TryDeleteStaging(StagedFile staged)
	{
		Ensure.NotNull(staged);
		return TryDeleteFile(staged.Path);
	}

	/// <inheritdoc />
	public void RecomputeTotalBytes() =>
		Interlocked.Exchange(ref _totalBytes, Enumerate().Sum(stored => stored.Size));

	private bool TryDeleteFile(AbsoluteFilePath path)
	{
		try
		{
			if (fileSystem.File.Exists(path))
			{
				fileSystem.File.Delete(path);
			}

			return true;
		}
		catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
		{
			// On Windows a file another request has open cannot be deleted. Skipping it and
			// retrying on the next sweep is correct; the alternative is failing a live transfer.
			logger.LogDebug(failure, "Could not delete {Path}; leaving it for the next sweep.", (string)path);
			return false;
		}
	}

	private async Task<bool> MatchesAsync(
		AbsoluteFilePath path,
		string oid,
		CancellationToken cancellationToken)
	{
		byte[] digest = new byte[hashProvider.HashLengthBytes];

		await using Stream stream = fileSystem.FileStream.New(
			path, FileMode.Open, FileAccess.Read, FileShare.Read);

		if (!await hashProvider.TryHashAsync(stream, digest, cancellationToken).ConfigureAwait(false))
		{
			return false;
		}

		return string.Equals(Convert.ToHexStringLower(digest), oid, StringComparison.OrdinalIgnoreCase);
	}

	private AbsoluteFilePath ObjectPath(string upstream, string oid) => fileSystem.Path
		.Combine(_root, upstream, ObjectsDirectoryName, oid[..2], oid[2..4], oid)
		.As<AbsoluteFilePath>();

	private AbsoluteDirectoryPath StagingDirectory(string upstream) => fileSystem.Path
		.Combine(_root, upstream, StagingDirectoryName)
		.As<AbsoluteDirectoryPath>();

	/// <summary>
	/// Rejects anything that is not exactly 64 lowercase hex characters.
	/// </summary>
	/// <remarks>
	/// Object ids reach this store from a token the proxy itself signed, so this check is defense
	/// in depth rather than the only guard. It is still worth having: it is the difference between
	/// a bug in the token layer being a cache miss and being a path traversal.
	/// </remarks>
	private static bool IsValidOid([NotNullWhen(true)] string? oid)
	{
		if (oid is null || oid.Length != OidLength)
		{
			return false;
		}

		foreach (char character in oid)
		{
			if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsValidUpstream([NotNullWhen(true)] string? upstream)
	{
		if (string.IsNullOrEmpty(upstream))
		{
			return false;
		}

		foreach (char character in upstream)
		{
			if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '-' or '_'))
			{
				return false;
			}
		}

		return true;
	}
}
```

Note the `Enumerate` filter that skips files whose name is not a valid oid. It keeps a stray file dropped into the tree by hand from being reported as a cached object, and therefore from being counted or evicted as one.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ObjectStoreTests"`
Expected: PASS.

If `MockFileSystem` does not honor `SetLastAccessTimeUtc` or `CreationTimeUtc`, check the `Testably.Abstractions.Testing` version's support before changing the production code, since the explicit access time is load-bearing for eviction.

- [ ] **Step 6: Build clean and run the whole suite**

Run: `dotnet build --no-incremental && dotnet test`
Expected: zero warnings, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add GitLfsCache/Storage GitLfsCache.Tests/Storage
git commit -m "[minor] Add content-addressed object store with verify-before-publish"
```

---
