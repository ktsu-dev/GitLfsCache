# ktsu.GitLfsCache

> A caching reverse proxy for the Git LFS HTTP API. Point `lfs.url` at it and object transfers come from a local store instead of crossing the internet twice.

[![License](https://img.shields.io/github/license/ktsu-dev/GitLfsCache.svg?label=License&logo=nuget)](LICENSE.md)
[![NuGet Version](https://img.shields.io/nuget/v/ktsu.GitLfsCache.Tool?label=Stable&logo=nuget)](https://nuget.org/packages/ktsu.GitLfsCache.Tool)
[![GitHub Actions Workflow Status](https://img.shields.io/github/actions/workflow/status/ktsu-dev/GitLfsCache/dotnet.yml?label=Build&logo=github)](https://github.com/ktsu-dev/GitLfsCache/actions)

## Introduction

Many short-lived clients pulling the same large objects is the situation this exists for: continuous integration pods cloning one repository over and over, where upstream bandwidth and clone latency dominate everything else.

`ktsu.GitLfsCache` sits between those clients and their forge. Every Batch API call is relayed upstream with the client's own credentials, so upstream remains the sole authority on who may read what. The transfer URLs in the response are rewritten to point back at the proxy, and object bytes are served from a local content-addressed store. A miss is fetched from upstream once and written to the store on the way through, so the next request for that object is local.

It is not an LFS server. It never becomes the authority for an object it did not obtain from an upstream, and it makes no access decisions of its own.

## Features

- **Batch relay, never short-circuited.** Every Batch call reaches upstream, even when the proxy already holds every object, so a cached object can never let a client skip an access check.
- **One upstream fetch per object.** Concurrent misses for the same object are coalesced: the first request fetches, the rest wait and then read from the store.
- **Verified before it is stored.** Content is digested while it streams and published only if it hashes to the object id it claims, so a truncated or tampered transfer can never become a cache hit.
- **Store-on-write for pushes.** An upload is relayed upstream and cached at the same time, so a just-pushed object is not a guaranteed miss.
- **Stateless replicas.** Rewritten URLs carry their own encrypted, authenticated context, so any replica can serve any URL with no shared cache and no sticky sessions.
- **Bounded disk use.** A byte budget with least-recently-used eviction, sweeping to a low-water mark so it does not thrash at the boundary.
- **Many upstreams from one deployment.** Each is addressed by the first path segment.
- **One lock listing for everyone.** `GET .../locks` is answered from a snapshot, so clients polling for lock state stop multiplying that traffic upstream. Upstream still authorizes every reader.
- **Batched locking.** A proxy extension takes many paths at once and fans them out in parallel under a rate limiter, instead of one sequential request per path.
- **Transparent to what it does not model.** Any other Git LFS endpoint is relayed verbatim.
- **One binary, two shapes.** The same build is the `gitlfscache` dotnet tool and the container image, so they cannot drift.

## Installation

### As a dotnet tool

```bash
dotnet tool install --global ktsu.GitLfsCache.Tool
```

### As a container

```bash
docker pull ghcr.io/ktsu-dev/gitlfscache:latest
```

## Usage

### Running locally

```bash
gitlfscache --port 8080 \
  --store /var/cache/gitlfscache \
  --max-size 100GB \
  --upstream github=https://github.com \
  --allow github='**'
```

Or point it at a configuration file and pass nothing else:

```bash
gitlfscache --config /etc/gitlfscache.json
```

Every flag except `--allow` is optional, and `--allow` is only required when the upstream it belongs to was declared on the command line rather than in configuration. Omit `--store` and it uses a per-user application data directory. Omit `--token-key` and one is generated for the run, with a warning: transfer URLs already handed out stop working when the process restarts, and a second instance cannot serve them.

`--upstream` is repeatable, and the name becomes the first path segment clients address:

```bash
gitlfscache --upstream github=https://github.com --allow github='studio/**' \
            --upstream ado=https://dev.azure.com/myorg --allow ado='myproject/**'
```

`--allow` is required at least once per upstream, and is also repeatable. It names the repository paths that upstream may be used for, and the proxy refuses to start without it. Pass `'**'` to allow every repository.

Requiring it is deliberate. Without a list, anyone who can reach the proxy can make it cache objects from any repository they can read, spending the byte budget and evicting the working set that the cache was deployed for. It is not an access control and never grants anything: upstream still authorizes every call with the client's own credential.

### Pointing a client at it

```bash
git config lfs.url https://lfs-cache.example.com/github/owner/repo.git/info/lfs
```

One setting is enough. The proxy is a complete replacement for the upstream LFS endpoint, not only a Batch interceptor, so anything it does not terminate itself is relayed.

**One thing that catches people out:** the git credential helper keys credentials by host. A client pointing `lfs.url` at the proxy must have its upstream token stored against the *proxy* host, not the forge host. Otherwise git has no credential to send, the proxy has none to relay, and upstream refuses the batch call.

### Configuration

Nothing has to be passed as a flag. Configuration comes from three places, each overriding the one before it:

1. **`appsettings.json` in the directory you run from.** Note that this is the working directory, not where the tool is installed, so the file ships with the container but a tool user provides their own.
2. **A file named with `--config`**, which can live anywhere: `gitlfscache --config /etc/gitlfscache.json`. It is layered over the working-directory file rather than replacing it, so an explicit file only has to carry what differs. A path that does not exist is reported by name and the process exits rather than starting on defaults.
3. **Environment variables**, using `__` as the section separator (`GitLfsCache__Store__MaxSize`, `GitLfsCache__Upstreams__github__BaseUrl`). This is how the Kubernetes base configures everything.

The flags are a convenience over the same settings and win over all three, so `--max-size 3GB` beats a `--config` file asking for 9GB.

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
      "StagingMaxAge": "06:00:00",
      "MaintenanceInterval": "00:05:00"
    },
    "Fetch": { "FollowerTimeout": "00:05:00" },
    "Locks": {
      "Enabled": true,
      "ListTtl": "00:00:15",
      "AdmissionTtl": "00:01:00",
      "MaxFanOutConcurrency": 8
    },
    "Upstreams": {
      "github": { "BaseUrl": "https://github.com", "Repositories": ["studio/**"] },
      "ado": { "BaseUrl": "https://dev.azure.com/myorg", "Repositories": ["myproject/**"] }
    }
  }
}
```

| Setting | Meaning |
|---|---|
| `PublicBaseUrl` | The URL clients actually use. Optional: when unset, each transfer URL is derived from the incoming request, which requires the ingress to send `X-Forwarded-Proto` and `X-Forwarded-Host`. Setting it explicitly is safer, because only the operator knows for certain what clients addressed. |
| `TokenKeys` | Base64 encoded 32 byte keys protecting rewritten transfer URLs. A list, so a key can be rotated without breaking transfers in flight: put the new key first and keep the old one until the token lifetime has elapsed. |
| `TokenLifetime` | How long a rewritten transfer URL stays valid. |
| `Store:MaxSize` | Byte budget, accepting decimal (`500GB`) and binary (`500Gi`) suffixes. |
| `Store:LowWaterMark` | The fraction of the budget a sweep reduces the store to. |
| `Store:StagingMaxAge` | How long an orphaned staging file from a crashed write survives. |
| `Fetch:FollowerTimeout` | How long a request waits for another request's fetch before fetching upstream itself. |
| `Locks:Enabled` | Whether the proxy terminates any part of the locking API. Set false to relay every lock route, which is what it did before this existed and the fallback if the cache is ever suspected of being wrong. |
| `Locks:ListTtl` | How long a lock listing is served before it is refreshed. Short deliberately: see below. |
| `Locks:AdmissionTtl` | How long an upstream authorization is trusted before it is proven again. Must be at least `ListTtl`, and startup refuses otherwise. |
| `Locks:RefreshTimeout` | How long a request waits for another request's listing walk before walking itself. |
| `Locks:MaxSnapshotLocks` | Above this many locks a repository is relayed rather than cached, so one enormous repository cannot consume memory without bound. |
| `Locks:MaxFanOutConcurrency` | How many lock calls may be in flight against one upstream at a time, across every request in the process. The right value per forge has to be found by measurement. |
| `Locks:MaxFanOutPaths` | The most paths one batched request may carry. Beyond this the request is refused rather than accepted and throttled part way through. |
| `Locks:MaxFanOutRetries` | How many times a throttled lock call is retried before it is reported as failed. |
| `Upstreams:<name>:Repositories` | Required. Path patterns this upstream may serve, matched against the whole path after the upstream key, with `*` inside one segment and `**` across segments. An entry normally ends in `**`, because the path continues into `info/lfs/...`. Use `**` to allow everything. |

Configuration is validated at startup and the process refuses to run on a bad value, reporting every problem at once and naming the setting each came from. That includes a store root that is not writable: a cache proxy whose volume is missing looks healthy from the outside and then fails every transfer, which is worse than a pod that will not start and says why.

### Kubernetes

A kustomize base lives in [`deploy/k8s/`](deploy/k8s/). Create the token key secret first, since it is deliberately not in the base:

```bash
kubectl create secret generic gitlfscache-tokens \
  --from-literal=tokenKey="$(head -c 32 /dev/urandom | base64)"

kubectl apply -k deploy/k8s
```

Three things about that base are worth knowing before you change it.

**It is a StatefulSet, not a Deployment.** The cache lives on a read-write-once volume, which cannot be shared between pods. Transfer URLs are stateless so any replica can serve any URL, but each replica holds its own cache and the hit rate dilutes as replicas grow. Start at one replica with a large volume and scale out only when a single pod saturates.

**The ingress annotations are load bearing.** Nginx buffers request bodies by default, which defeats the streaming upload tee and caps object size at whatever the controller is willing to buffer. `proxy-request-buffering: "off"` and `proxy-body-size: "0"` are what let a large push stream through, and the generous read and send timeouts are what stop a large transfer being cut off partway. These are nginx-specific: a different ingress controller needs its own equivalents, and the failure mode if they are missed is silent, with small objects working and large ones failing.

**Provision the volume above the byte budget.** Eviction runs on an interval, so the store can sit over budget between sweeps.

Memory does not scale with object size, because nothing is ever buffered whole. A 20 GB object and a 20 MB object have the same memory profile, which makes the resource limits straightforward.

### File locking

The locking API is part of the Git LFS HTTP API, addressed as a suffix of whatever `lfs.url` points at, so a client already sends its lock traffic here. The proxy terminates two parts of it.

**The listing is cached.** `GET .../locks` is answered from an in-memory snapshot per repository. The first client to ask after the snapshot goes stale walks every upstream cursor page and publishes the result; everyone else reads it. A client polling on a timer therefore costs upstream one walk per `ListTtl` however many clients are running. The proxy also assembles the pages itself, so a client gets the whole listing in one round trip instead of walking cursors over a wide-area link.

Nothing is served to a caller upstream has not recently accepted. A caller with no current admission is checked with a single-page request under its own credential before it sees anything, and an admission only ever exists because a real upstream call succeeded. **The cache never decides who may read locks**; it only remembers, briefly, what upstream already decided. The consequence to be aware of is that a credential revoked upstream can still read listings for up to `AdmissionTtl`.

Creation and release are never terminated, only relayed, because upstream is the only thing that may grant or release a lock. A successful one drops the snapshot immediately, so a client that just took a lock sees it.

**Staleness costs a retry, not a conflict.** Two clients can both see a file unlocked within `ListTtl` and both try to lock it; upstream grants one and refuses the other exactly as it would have without the cache. What the proxy cannot see is a lock taken outside it, through a forge's web interface or a client not configured through the proxy, and `ListTtl` is the only thing bounding how long that stays invisible. That is why it is short and not a knob to turn up casually.

#### Batched locking

git-lfs issues one request per path, one after another, so locking several hundred assets over a wide-area link is several hundred sequential round trips. `POST .../locks/batch` is a proxy extension that takes them at once:

```json
{ "operation": "lock", "paths": ["Content/A.uasset", "Content/B.uasset"] }
```

`operation` is `lock` or `unlock`; `unlock` also accepts `ids`, and `force`. Both accept a `ref`. The response is always 200 when the request was well formed, with one result per item in the order they were sent, because partial success is the normal outcome and failing the whole request would discard the half that worked:

```json
{ "results": [
    { "path": "Content/A.uasset", "ok": true,  "lock": { "id": "871", "owner": { "name": "someone" } } },
    { "path": "Content/B.uasset", "ok": false, "status": 409, "message": "already locked" }
  ] }
```

Each item is still one upstream call carrying the caller's own credential, so upstream decides every lock individually under the caller's identity. The proxy chooses only the order and the concurrency. **This is not atomic and cannot be** over an API with no transaction: a client that disconnects part way through leaves the locks already granted still granted, so treat the result array as authoritative and reconcile.

Because it is an extension, a stock git-lfs will not use it — a client has to be taught to. Note that `Locks:Enabled: false` makes this route 404 rather than relaying it, since no upstream has such an endpoint.

Forge rate limits are the expected failure here, not an edge case. A refusal carrying `Retry-After` pauses every call to that upstream for the stated duration, and the item is retried up to `MaxFanOutRetries`. Watch the throttled-items counter and lower `MaxFanOutConcurrency` if it is ever non-zero.

### Health and metrics

`/healthz` reports liveness, `/readyz` reports readiness gated on a writable store and valid configuration.

Counters are published through `System.Diagnostics.Metrics` under the meter `ktsu.GitLfsCache`, with no exporter bundled: hits, misses, bytes served from cache, bytes fetched upstream, bytes relayed on upload, objects stored, verification failures, coalesced waits, and rejected tokens. The hit and miss pair is the one to watch. A low hit ratio means the proxy is costing latency without saving bandwidth, which usually means the volume is too small for the working set.

The locking API has its own counters: lock list hits, refreshes, refresh failures, refresh waits, admission probes, admission rejections, and for batched locking the items attempted, items succeeded, and items throttled. **Lock list hits against refreshes** is the pair to watch, and is directly the multiple by which upstream lock traffic has been reduced. **Anything but zero on throttled items** means `MaxFanOutConcurrency` is above what that forge tolerates, and is the signal to turn it down.

## How it works

A rewritten transfer URL carries an opaque token holding the upstream URL, its headers, the object id, the size, the upstream key, and an expiry. The payload is encrypted, authenticated with a message authentication code over the whole envelope, and Base64url encoded into a query parameter.

That single decision buys three things. Replicas need no shared state, because everything required to serve a URL travels inside it. Holding a valid token is proof upstream approved that object for that client, so cached bytes never reach a client upstream did not clear. And because the token always carries the upstream action, an object evicted between the Batch call and the transfer still resolves.

The store is content-addressed, one tree per upstream:

```
<root>/<upstream>/objects/<first two oid characters>/<next two>/<oid>
<root>/<upstream>/staging/<guid>.tmp
```

Staging shares the volume with the objects so publishing is an atomic rename. Access times are stamped explicitly on every hit rather than read from the filesystem, because `noatime` and `relatime` mounts make filesystem access times unreliable and eviction depends on them.

For the full design, including the alternatives that were rejected and why, see [the design document](docs/superpowers/specs/2026-08-18-gitlfscache-design.md).

## Contributing

Contributions are welcome. Please open an issue or a pull request.

## License

MIT License. Copyright (c) ktsu.dev. See [LICENSE.md](LICENSE.md).
