# ktsu.GitLfsCache

> A caching reverse proxy for the Git LFS HTTP API. Point `lfs.url` at it and object transfers come from a local store instead of crossing the internet twice.

[![License](https://img.shields.io/github/license/ktsu-dev/GitLfsCache.svg?label=License&logo=nuget)](LICENSE.md)
[![NuGet Version](https://img.shields.io/nuget/v/ktsu.GitLfsCache?label=Stable&logo=nuget)](https://nuget.org/packages/ktsu.GitLfsCache)
[![NuGet Version](https://img.shields.io/nuget/vpre/ktsu.GitLfsCache?label=Latest&logo=nuget)](https://nuget.org/packages/ktsu.GitLfsCache)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ktsu.GitLfsCache?label=Downloads&logo=nuget)](https://nuget.org/packages/ktsu.GitLfsCache)
[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/ktsu-dev/GitLfsCache?label=Commits&logo=github)](https://github.com/ktsu-dev/GitLfsCache/commits/main)
[![GitHub contributors](https://img.shields.io/github/contributors/ktsu-dev/GitLfsCache?label=Contributors&logo=github)](https://github.com/ktsu-dev/GitLfsCache/graphs/contributors)
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
- **Transparent to what it does not model.** Any other Git LFS endpoint, including the locks API, is relayed verbatim.
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
  --upstream github=https://github.com
```

Every flag is optional. Omit `--store` and it uses a per-user application data directory. Omit `--token-key` and one is generated for the run, with a warning: transfer URLs already handed out stop working when the process restarts, and a second instance cannot serve them.

`--upstream` is repeatable, and the name becomes the first path segment clients address:

```bash
gitlfscache --upstream github=https://github.com --upstream ado=https://dev.azure.com/myorg
```

### Pointing a client at it

```bash
git config lfs.url https://lfs-cache.example.com/github/owner/repo.git/info/lfs
```

One setting is enough. The proxy is a complete replacement for the upstream LFS endpoint, not only a Batch interceptor, so anything it does not terminate itself is relayed.

**One thing that catches people out:** the git credential helper keys credentials by host. A client pointing `lfs.url` at the proxy must have its upstream token stored against the *proxy* host, not the forge host. Otherwise git has no credential to send, the proxy has none to relay, and upstream refuses the batch call.

### Configuration

Every value binds from configuration, so anything below is settable by environment variable using `__` as the section separator (`GitLfsCache__Store__MaxSize`).

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
    "Upstreams": {
      "github": { "BaseUrl": "https://github.com" },
      "ado": { "BaseUrl": "https://dev.azure.com/myorg" }
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

### Health and metrics

`/healthz` reports liveness, `/readyz` reports readiness gated on a writable store and valid configuration.

Counters are published through `System.Diagnostics.Metrics` under the meter `ktsu.GitLfsCache`, with no exporter bundled: hits, misses, bytes served from cache, bytes fetched upstream, bytes relayed on upload, objects stored, verification failures, coalesced waits, and rejected tokens. The hit and miss pair is the one to watch. A low hit ratio means the proxy is costing latency without saving bandwidth, which usually means the volume is too small for the working set.

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
