# ktsu.GitLfsCache Design

Date: 2026-08-18
Status: implemented. See the As built section for where the code departed from this document.

## Purpose

A caching reverse proxy for the Git LFS HTTP API. Clients point `lfs.url` at the proxy instead of at their forge. The proxy forwards every Batch API call upstream so upstream remains the sole authority on access, rewrites the object transfer URLs in the response to point back at itself, and serves object bytes from a local content-addressed store. A miss is fetched from upstream once and stored on the way through, so the next request for that object is local.

The target situation is many short-lived clients on one network repeatedly pulling the same large objects, for example continuous integration pods cloning the same repository, where upstream bandwidth and clone latency dominate.

Ships two ways from one binary: a dotnet tool (`gitlfscache`) for local and single-machine use, and a container image for Kubernetes.

## Non-goals

- Not an LFS server. The proxy never becomes the authority for an object it did not obtain from an upstream.
- No authorization decisions of its own. Every Batch call is relayed upstream with the client's credentials, and upstream's answer is respected.
- No object rewriting, deduplication across upstreams, or content transformation.
- No support for custom transfer adapters. Only the `basic` transfer adapter is handled specially, and anything else falls through to the relay.

## Decisions

Each of these was chosen deliberately and the alternative is recorded, because the alternatives are all defensible and a future reader will wonder.

| Decision | Chosen | Rejected alternative |
|---|---|---|
| Transfer directions | Cache downloads, relay uploads while teeing into the store | Downloads only, which leaves a just-pushed object as a guaranteed miss |
| Miss path | Every href points at the proxy, which tees the upstream fetch to client and store at once | Returning the upstream href on a miss and warming in the background, which doubles upstream bandwidth per miss and requires clients to reach upstream |
| Authorization | Relay the client's `Authorization` header on every Batch call, always | Proxy-held service credentials, which make the proxy an authorization bypass for anyone who can reach it |
| Upstream count | Many, keyed by the first path segment | One per deployment, which needs a deployment and a volume per remote |
| Eviction | Byte budget with least-recently-used eviction | Unbounded storage, which turns a full volume into an outage instead of a cold cache |
| Store namespacing | One object tree per upstream key | One shared tree, which is safe on content addressing grounds but widens the blast radius of a token bug |
| Project structure | Core library owns domain and endpoint mapping, thin host project | Framework-free core with endpoints in the host, which puts the riskiest code where it is hardest to test |
| SDK support | Web and container properties inline in the csproj for now | A `ktsu.Sdk.Service` variant first, which cannot be designed well with zero consumers |

The SDK variant is a deliberate follow-up. Once the container and the tool both work, the proven property set is worth extracting into `ktsu.Sdk.Service`: a framework reference on `Microsoft.AspNetCore.App`, `appsettings*.json` content globs, container defaults, and runtime identifiers narrowed to Linux. That extraction is out of scope here and gets its own spec.

## Request flow

Clients configure `lfs.url` as `https://cache.example/<upstream>/<repo path>/info/lfs`, where `<upstream>` is a configured key. The proxy is a complete replacement for the upstream LFS endpoint, not only a Batch interceptor, which is what makes a single `lfs.url` setting sufficient.

### Batch, POST to `.../objects/batch`

1. Resolve the upstream key to a base URL. An unknown key is a 404 before anything else happens.
2. Forward the request body and the client's `Authorization` header to upstream unchanged.
3. Relay any non-success response verbatim, including status and body, so the client sees upstream's real answer rather than a proxy interpretation.
4. For each object in a success response, rewrite each action's `href` to a proxy URL and drop the action's `header` map, since the credentials it carries now live inside the token. Set `expires_in` from the token lifetime.
5. Objects that upstream returned with an `error` and no actions pass through untouched.

The store is never consulted during a Batch call, and a Batch call is never served from cache even when every object is already local. Short-circuiting it would move the authorization decision from upstream into the proxy, which is the one thing this design refuses to do.

### The href token

This is the load-bearing decision, so it is specified precisely.

Each rewritten href carries one opaque token containing the upstream `href`, the upstream `header` map, the oid, the size, the upstream key, and an absolute expiry. The token is serialized, encrypted, authenticated, and Base64url encoded into the proxy URL as a query parameter named `t`, so a rewritten href reads `https://cache.example/github/owner/repo.git/info/lfs/objects/<oid>?t=<token>`. A query parameter rather than a path segment keeps the oid in a fixed position and keeps the token out of the routing template.

It buys three things at once:

- **Stateless replicas.** Any replica can serve any href with no shared cache, no sticky routing, and nothing to lose on a restart.
- **Proof of authorization.** Holding a valid token means upstream approved that object for that client during a Batch call the proxy relayed. Cached bytes are therefore never served to a client upstream did not clear.
- **Eviction resilience.** Because the token always carries the upstream action, an object evicted between the Batch call and the transfer still resolves, by fetching upstream.

Construction, in order:

1. Serialize the payload as JSON with `System.Text.Json`. An Essentials `ISerializationProvider` would add indirection with no benefit here: the payload is internal to the proxy and its format is never a configurable choice.
2. Derive an encryption key and a separate authentication key from the configured key using `HKDF`.
3. Encrypt with Essentials' `Aes` provider and a fresh initialization vector per token.
4. Compute `HMACSHA256` over the initialization vector and the ciphertext.
5. Concatenate version byte, key identifier, initialization vector, ciphertext, and tag, then encode with `System.Buffers.Text.Base64Url`. Essentials' `Base64` provider is not used here because it emits standard base64, whose `+`, `/`, and `=` characters are not URL-safe.

Verification reverses this, comparing tags with `CryptographicOperations.FixedTimeEquals` before attempting decryption, and rejecting an expired payload.

Essentials' `Aes` provider uses `Aes.Create()` defaults, meaning CBC with PKCS7 padding and no authentication. A token that is only encrypted is malleable, and this token carries an upstream credential and the oid that selects which bytes get served, so tampering has to be detectable rather than merely inconvenient. `HMACSHA256` and `HKDF` come from the base class library because every Essentials hash provider is unkeyed. A keyed-hash provider is a real gap in Essentials and is worth contributing back separately.

`TokenKeys` is a list. Encryption always uses the first entry and decryption tries each in turn, so a key can be rotated without breaking transfers already in flight.

### Object download, GET to `.../objects/<oid>`

1. Verify the token, and check that its oid matches the path. Reject on failure with 403.
2. If the request carries a `Range` header and the object is not stored, forward the range upstream and stream the response without storing anything. A partial object must never enter the store.
3. On a store hit, set the object's last access time and stream from disk with range support.
4. On a miss, become the leader or a follower for that oid, per Concurrent misses below.
5. A leader opens the upstream fetch using the action inside the token, tees the bytes to the client and to a staging file at once, digests them as they are written, compares the digest to the oid, and publishes by atomic rename only on a match.

Verification digests the stream as it is written rather than hashing the finished staging file. Essentials 2.0.0's `IHashProvider` offers only a synchronous `TryHash(Stream, ...)` with no incremental or async-stream form, so hashing afterwards would mean reading a multi-gigabyte object a second time while blocking a thread. Digesting during the write costs one pass and no blocking. SHA256 is therefore taken from the base class library's `IncrementalHash`, and is hard-coded rather than injected because Git LFS defines object ids as SHA256 digests, so there is no alternative to select.

An oid mismatch discards the staging file, logs it, and fails that request. Unverified bytes are never published and never served as a hit.

### Object upload, PUT to `.../objects/<oid>`

The upload action is rewritten the same way as a download action. The proxy relays the request body upstream as it arrives while teeing to a staging file, and publishes into the store only after upstream returns success. A local write failure is logged and never fails the upload, because the client's push succeeding matters more than the cache warming.

The `verify` action, when upstream supplies one, is rewritten and relayed too, so a client never needs upstream reachability or credentials for any part of a push.

### Relay, everything else

Any other path under an upstream prefix, including the locks API, is relayed verbatim with the client's headers. This is what lets one `lfs.url` fully replace the upstream, and it means an LFS feature the proxy does not understand degrades to plain proxying rather than failing.

## Components

Each unit has one purpose, a narrow interface, and can be tested without the others.

| Unit | Responsibility | Depends on |
|---|---|---|
| `UpstreamRegistry` | Maps an upstream key to a base URL, rejects unknown keys | configuration |
| `IHrefTokenCodec` | Encrypts, authenticates, decodes, and expires the token | Essentials `IEncryptionProvider`, `IEncodingProvider`, `ISerializationProvider` |
| `BatchRewriter` | Pure transform from an upstream batch response plus request context to a rewritten response, with no input or output of its own | `IHrefTokenCodec` |
| `IObjectStore` | `OpenRead`, `OpenStaging`, `PublishAsync`, `Touch`, `Enumerate` over a content-addressed tree | `IFileSystem`, `ktsu.Semantics.Paths` |
| `IEvictionPolicy` | Selects objects to delete when the byte budget is exceeded | `IObjectStore` |
| `FetchCoalescer` | Ensures one upstream fetch per oid in flight | `IObjectStore` |
| `UpstreamClient` | Relays batch calls, opens object fetches and upload relays, applies token headers | `HttpClient` |
| `TeeStream` | Copies one source to two sinks, the store sink failing soft | none |
| `SizeParser` | Parses `500GB` and `500Gi` style values to bytes | none |
| endpoint module | Maps batch, object GET and PUT, relay, and health probes | all of the above |

`BatchRewriter`, `IHrefTokenCodec`, and `SizeParser` are pure and carry the densest tests.

## Object store and eviction

Layout, one tree per upstream key, with `ktsu.Semantics.Paths` types for the root and object paths:

```
<root>/<upstream>/objects/<first two oid characters>/<next two>/<oid>
<root>/<upstream>/staging/<guid>.tmp
```

Staging sits on the same volume as the objects so publishing is an atomic rename. The two-level fan-out mirrors the git-lfs client's own layout and keeps directory sizes reasonable into the hundreds of thousands of objects.

Namespacing per upstream duplicates bytes when two upstreams hold the same object, and content addressing means those bytes are identical. The duplication is accepted to bound the blast radius if the token codec is ever wrong. A single shared tree is recorded as a deferred option, not a configuration flag.

### Access tracking

The proxy sets `LastAccessTimeUtc` explicitly on every hit through `IFileSystemProvider` rather than trusting filesystem access times, which `noatime` and `relatime` mounts make unreliable. That is one cheap metadata write per hit, it needs no index to persist or repair after a crash, it survives pod restarts, and the mock filesystem supports it, so eviction is testable with no disk involved.

### Eviction

A total-byte counter is built by a full scan at startup and maintained incrementally. Crossing the byte budget triggers a sweep that deletes coldest-first down to a low-water mark, defaulting to 90 percent of the budget, so the sweep does not thrash at the boundary.

Deleting a file that another request currently has open is safe on Linux and throws on Windows, so the sweep catches the I/O error and skips that object, retrying on the next sweep. Staging files orphaned by a crashed write are removed by age on a separate timer and are never touched by the object sweep.

### Concurrent misses

Fifty pods cloning at once must not become fifty upstream fetches, which is the entire point of the cache. Requests for the same missing oid are coalesced. The first request is the leader and tees to client and store. Later arrivals wait for the leader to publish, then serve from disk. If the leader fails, or the wait exceeds a configured timeout, a follower falls back to fetching upstream itself.

Followers therefore pay the leader's full download latency before their first byte. Having followers tail the leader's staging file so they stream concurrently is the obvious improvement and is deliberately deferred.

## Configuration

Standard options binding, so every value is settable by environment variable (`GitLfsCache__Store__MaxSize`) for Kubernetes.

```json
{
  "GitLfsCache": {
    "PublicBaseUrl": "https://lfs-cache.example.com",
    "TokenKeys": ["<base64, 32 bytes>"],
    "TokenLifetime": "01:00:00",
    "Store": {
      "Root": "/var/lib/gitlfscache",
      "MaxSize": "500GB",
      "LowWaterMark": 0.9,
      "StagingMaxAge": "06:00:00"
    },
    "Fetch": { "FollowerTimeout": "00:05:00" },
    "Upstreams": {
      "github": { "BaseUrl": "https://github.com" },
      "ado": { "BaseUrl": "https://dev.azure.com" }
    }
  }
}
```

`MaxSize` is a string with a unit suffix, parsed once at startup, accepting both decimal (`500GB`) and binary (`500Gi`) forms and failing fast with a clear message on anything else.

`PublicBaseUrl` is optional. When absent, hrefs are built from the request host and forwarded headers, which requires `ForwardedHeaders` middleware configured for the ingress. An explicit value always wins.

Startup validation refuses to run when there are no upstreams, when the store root is not writable, when a token key is not 32 bytes, or when `LowWaterMark` is outside a sensible range. Failing at startup is preferable to failing on the first clone.

## Failure handling

Every failure mode favors correctness over cache warmth.

- An upstream batch error relays verbatim, status and body included.
- An oid mismatch after a fetch discards the staging file, logs, and fails that request.
- Any store write failure degrades to plain pass-through, so the client still gets its bytes and the cache stays cold.
- An invalid, expired, or tampered token is 403 with no detail, and the failure is logged with the reason.
- A `Range` header on a miss forwards upstream and does not store.
- A client disconnecting mid-transfer cancels the upstream fetch and discards staging, since a truncated object must not be published.

## Deployment

### Container

No Dockerfile. The .NET SDK's built-in container support (`dotnet publish` with the `PublishContainer` target) builds and pushes directly to a registry with no Docker daemon, so it runs from the existing `windows-latest` CI runner and still produces Linux images.

- Base image `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled`. Minimal, no shell, already non-root. The core SDK already sets `InvariantGlobalization=true`, so no ICU is needed and chiseled is a clean fit.
- `ContainerRuntimeIdentifiers` of `linux-x64;linux-arm64` produces a multi-architecture image index.
- OCI labels populated from the metadata files the SDK already reads.
- Pushed to `ghcr.io/ktsu-dev/gitlfscache`, authenticating with the built-in `GITHUB_TOKEN`, so no new secrets are needed.

### Dotnet tool

The same project packs as `ktsu.GitLfsCache.Tool` and installs the `gitlfscache` command, derived by `ktsu.Sdk.Tool` from the solution name. The container runs that same binary, so the tool and the container cannot drift.

The tool takes friendly flags through `System.CommandLine` rather than requiring configuration-key style arguments: `--port`, `--store`, `--max-size`, `--token-key`, and a repeatable `--upstream github=https://github.com`. With no `--token-key`, an ephemeral key is generated and a warning states that outstanding hrefs will not survive a restart.

### Kubernetes

A kustomize base in `deploy/k8s/`.

A StatefulSet with `volumeClaimTemplates`, not a Deployment, because a read-write-once volume cannot be shared between pods. Tokens are stateless so any replica can serve any href, but each replica holds its own cache, so hit rate dilutes as replica count grows. Start at one replica with a large volume and scale out only when a single pod saturates.

Two operational traps are built into the base rather than left to be discovered:

- Nginx ingress buffers request bodies by default, which would defeat the streaming upload tee and cap object size. The base sets `proxy-request-buffering: "off"`, `proxy-body-size: "0"`, and generous read and send timeouts.
- `readOnlyRootFilesystem: true` needs an `emptyDir` mounted at `/tmp`. Staging lives on the cache volume, but the runtime still wants scratch space.

Also: `runAsNonRoot`, all capabilities dropped, `RuntimeDefault` seccomp profile, `/healthz` for liveness, `/readyz` for readiness gated on a writable store and valid configuration, a ConfigMap for plain settings, and a Secret for `TokenKeys`.

Memory does not scale with object size, because nothing is ever buffered whole. The tee moves data in small buffers, so a 20 GB object and a 20 MB object have the same memory profile, which makes resource limits straightforward to set.

### Instrumentation

`System.Diagnostics.Metrics` counters only, with no exporter dependency bundled: hits, misses, bytes served from cache, bytes fetched upstream, bytes evicted, coalesced waits, and verification failures. Adding an OpenTelemetry Prometheus exporter is a few lines in the host and is deferred rather than pinning a dependency on a guess about the scraping setup.

### Continuous integration

The standard ktsu `.github/workflows/dotnet.yml` handles build, test, version, changelog, and NuGet publishing unchanged. A container publish step is added after a successful release, gated on the release having produced a version.

## Repository layout

Modeled on the current `KtsuBuild` layout, which is the up to date convention. `CreateProject/create-new-project.ps1` is stale and must not be used: it emits old-style outer SDK attributes such as `ktsu.Sdk.Lib` with a version suffix, a `.sln` rather than a `.slnx`, `.Core`/`.CLI`/`.App`/`.Test` project names that no longer match convention, and it runs `dotnet format`, which this repository forbids on multi-target projects.

```
GitLfsCache/
  GitLfsCache/              core library, net10.0 only
                            FrameworkReference Microsoft.AspNetCore.App
                            domain plus AddGitLfsCache and MapGitLfsCache
  GitLfsCache.Tool/         thin Program.cs, PackAsTool, container target
  GitLfsCache.Tests/        net10.0, MSTest.Sdk
  deploy/k8s/               kustomize base
  docs/superpowers/specs/   this document
  .github/workflows/
  scripts/
  Directory.Packages.props
  GitLfsCache.slnx
  global.json
  README.md, DESCRIPTION.md, AUTHORS.md, TAGS.md, LICENSE.md
```

The core library pins `<TargetFrameworks>net10.0</TargetFrameworks>` explicitly, overriding the core SDK's eight-framework default, because an ASP.NET Core component has no reason to target netstandard2.0 and doing so would force Polyfill workarounds through the streaming code for no consumer.

`global.json` pins `msbuild-sdks` for `ktsu.Sdk` and `ktsu.Sdk.Tool` to the latest published version, verified at scaffold time rather than assumed.

## Testing strategy

MSTest with semantic asserts, per repository convention.

Unit tests:

- Token codec: round-trip, expiry, key rotation across a list, and tamper rejection with a bit flipped in each region of the token in turn.
- `BatchRewriter`: captured real payload shapes for GitHub and Azure DevOps, covering download, upload, per-object errors, objects with no actions, and a `verify` action.
- `SizeParser`: decimal and binary suffixes, no suffix, and malformed input.
- `IObjectStore` against the mock filesystem: publish and verify, oid mismatch rejection, access time update on a hit, and staging cleanup by age.
- Eviction: ordering by access time, stopping at the low-water mark, and skipping a file that cannot be deleted.
- `FetchCoalescer` with a controllable fake upstream: one fetch for many waiters, leader failure, and follower timeout fallback.

Integration tests build a real in-process `WebApplication` through the library's own `AddGitLfsCache` and `MapGitLfsCache` extensions, pointed at a stub upstream test server, and walk the clone-shaped path: batch, miss, download with tee, second batch, hit. Also upload relay with store-on-write, `Range` on a miss, the catch-all relay, an unknown upstream key, and a forged token.

### Manual verification

Automated tests do not cover a real `git lfs clone` against a real remote, and this is recorded as a manual step rather than left to look covered:

1. Run `gitlfscache` locally with one upstream pointing at a real forge.
2. Clone a repository with LFS objects through the proxy with a cold store, confirming the objects arrive and land in the store.
3. Clone again into a fresh directory, confirming the objects are served from the store and the hit counters move.
4. Push a new LFS object through the proxy, confirming upstream receives it and the store has a verified copy.
5. Confirm the container image runs the same scenarios in Kubernetes behind the ingress.

One operational note belongs in the README: the git credential helper keys credentials by host, so a client pointing `lfs.url` at the proxy must have its upstream token stored against the proxy host, not the forge host.

## Risks

Two things could invalidate part of this design, and both are cheap to check early rather than late.

**One project serving as both a dotnet tool and a container: this risk materialized, and the fallback was taken.** `ktsu.Sdk.Tool` deliberately clears `RuntimeIdentifiers`, because under `PackAsTool` the .NET SDK turns each entry into a separate runtime-specific tool package. `ContainerRuntimeIdentifiers` alone is not enough: a multi-architecture image also needs `RuntimeIdentifiers` set so that restore produces assets for each runtime, and setting it made `dotnet pack` emit `ktsu.GitLfsCache.Tool.linux-x64` and `ktsu.GitLfsCache.Tool.linux-arm64` alongside the real package. Nobody wants to install a linux-arm64 dotnet tool.

The recorded fallback was therefore taken: `GitLfsCache.Service` builds the container image and compiles `Program.cs` linked from `GitLfsCache.Tool`, so the tool and the container are still built from one source and cannot drift. Two details worth keeping:

- The failure only appears on SDK 10.0.400, which the CI runner has, not on 10.0.302. Neither the malformed OCI label nor the runtime identifier conflict reproduced on the development machine.
- The multi-architecture publish must run with `-m:1`. The per-runtime inner builds are not given runtime-qualified intermediate paths, so in parallel they both write `obj/Release/net10.0/*.FileListAbsolute.txt` and the publish fails on a locked file. That is the same racing-over-one-intermediate-directory problem `ktsu.Sdk.Tool` documents, surfacing from a different direction.

**A ktsu.Sdk bug found on the way.** `Sdk.Common.MetadataFiles.props` reads `PROJECT_URL.url` with `File.ReadAllText(...).Trim()` and assigns the whole thing to `PackageProjectUrl`. Every ktsu repository stores that file in Windows shortcut format, so the value becomes `[InternetShortcut]
URL=https://...`. NuGet silently drops `<projectUrl>` from the nuspec as a result, which is precisely the outcome the comment above that code says it was written to fix, and SDK 10.0.400 fails container publishing outright because the generated `org.opencontainers.image.url` label name contains it. This project sets `PackageProjectUrl` explicitly as a local workaround; the SDK should parse the `URL=` line.

**Ingress specifics.** The request-buffering annotations in the kustomize base are nginx-specific. A different ingress controller needs its own equivalent, and the failure mode if it is missed is silent: uploads work for small objects and fail at whatever size the controller buffers to. This belongs in the README as a deployment prerequisite, not only in the manifests.

## As built

Four places where the implementation departed from this document, recorded here rather than left for a
reader to discover by diffing:

- **Base64url comes from the base class library**, not Essentials' Base64 provider, which emits
  standard base64 and is not URL-safe. Noted inline above.
- **Objects are digested while streaming** rather than hashed from the finished staging file, because
  Essentials 2.0.0 has no incremental or async-stream hash. Noted inline above.
- **`IObjectStore` depends on `IFileSystem`** rather than Essentials' `IFileSystemProvider`. That
  interface is a marker over `System.IO.Abstractions.IFileSystem`, and depending on the underlying
  interface lets a mock filesystem stand in directly with no adapter. The Essentials native provider
  is still what satisfies it at runtime.
- **The store reader returns the stream** instead of following the `Try` pattern with an out
  parameter. A `Try` method that hands back a disposable cannot be wrapped in a `using` at the call
  site, which makes leaking a file handle on an early return easy.

The `ktsu.Essentials.HashProviders.SHA256` and serialization provider packages were consequently not
needed. Essentials is still used for AES encryption, the native filesystem provider, and
`UserDirectories`.

## Deferred

Recorded so they are choices rather than omissions:

- `ktsu.Sdk.Service`, extracted from this project's proven inline properties.
- Replacing the encrypt-then-authenticate construction with `AesGcm`, which is one primitive doing
  both jobs instead of three derived keys and a separate tag comparison. The current construction is
  correct, but it has more moving parts than the problem needs, and it exists mainly to keep the
  encryption inside Essentials.
- A keyed-hash provider contributed to Essentials, removing the direct `HMACSHA256` dependency.
- Followers tailing the leader's staging file so concurrent misses stream rather than wait.
- A single shared object tree across upstreams, trading blast radius for deduplication.
- A bundled OpenTelemetry Prometheus exporter.
- Pod disruption budget and horizontal autoscaling in the kustomize base.
- Helm packaging, if kustomize proves insufficient.
