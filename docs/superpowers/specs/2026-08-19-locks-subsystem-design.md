# ktsu.GitLfsCache Locks Subsystem Design

Date: 2026-08-19
Status: proposed.

## Purpose

Terminate the Git LFS locking API in the proxy so that the lock listing is fetched from upstream once per interval for a whole studio rather than once per client, and so that locking many files costs one client round trip instead of one per file.

The driving workload is the Unreal Engine source control plugin (an in-house fork of `ProjectBorealis/UEGitPlugin`). Three properties of it set every requirement here:

- `GitSourceControlRunner.cpp:37` runs a full status refresh on a fixed 30 second timer in every open editor.
- `RefreshLocks` (`GitSourceControlUtils.cpp:1513`) abandons per-file queries above `LocksThresholdForFullRefresh = 2` and pulls the entire repository lock list. Its own comment records that `git lfs locks` is slow regardless of how many files are passed. The cost is HTTP cursor pagination, so a repository with thousands of locks is tens of sequential round trips, per client, every 30 seconds.
- `RunLFSCommand(TEXT("lock"), ...)` chunks at `MaxFilesPerBatch = 50` and iterates the chunks serially, and git-lfs turns each path into its own `POST /locks`. Locking 500 assets is 500 sequential round trips.

## Why this belongs in this repository

The locking URLs are defined by the Git LFS specification as suffixes of the LFS server URL, and the client configuration surface has `lfs.url` and `lfs.pushurl` and no lock-specific endpoint. A separate service could not receive lock traffic without also receiving batch and transfer traffic, so it would have to reimplement the upstream registry, the credential relay, and the batch path. That would mean a second copy of the code that enforces "upstream is the sole authority", which is the one piece of this system that must never drift.

`LfsRouteParser` already classifies unrecognized paths as `Relay` specifically so that unmodelled LFS features degrade to proxying. This subsystem is that seam being used as intended.

## Non-goals

- Not a lock authority. The proxy never grants, denies, or invents a lock. Every state-changing call is relayed upstream under the caller's own credential and upstream's answer is returned.
- No service credentials, for any purpose, including cache warming. Every upstream call this subsystem makes is made with a requesting client's credential.
- `POST /locks/verify` is not cached. It partitions locks into `ours` and `theirs` by authenticated identity, so a shared cache entry would be wrong for every caller but one, and it is called on push rather than on the 30 second heartbeat, so caching it buys nothing.
- No cross-repository or cross-upstream sharing of lock state.

## Decisions

| Decision | Chosen | Rejected alternative |
|---|---|---|
| Who pays for a refresh | The first client to ask after the snapshot goes stale, using its own credential, with others coalesced behind it | A background refresher on a timer, which needs a service credential the design refuses to hold |
| Authorizing a cached read | Short-lived credential admission, proven by a real upstream call | Serving the snapshot to anyone who can reach the proxy, which makes the cache an authorization bypass |
| `verify` | Relayed verbatim | Cached per identity, which holds a credential-derived key for a call that happens once per push |
| Filtered list queries | Answered from the snapshot by filtering locally | Passed upstream, which leaves the plugin's threshold of 2 with nothing to gain |
| Pagination | The proxy walks upstream cursors and serves the assembled list, minting its own cursors from the snapshot when a client asks for pages | Relaying cursors, which preserves the round trip count that is the whole cost |
| Fan-out failure model | Always 200 with a per-item result array | Failing the whole request on any item, which makes a single 409 discard hundreds of successful locks |
| Fan-out concurrency limit | Per upstream, shared across all in-flight requests | Per request, which lets two clients batching at once collectively exceed forge rate limits |
| Repository allow-list | Required per upstream, no default | Optional with empty meaning allow everything, which leaves the decision to whoever forgets to make it |

## Route additions

`LfsRouteKind` gains four members, and `LfsRouteParser.Classify` gains four cases ahead of its `Relay` fallback. Paths are relative to the repository's `info/lfs` base.

The parser sees a path and no method, which is why there are four kinds and not five: listing and creation share the `locks` path and are told apart by the handler, exactly as `Transfer` already covers both the `GET` and the `PUT` on an object.

| Path | Kind | Method | Handling |
|---|---|---|---|
| `locks` | `Locks` | `GET` | Terminated. Served from the snapshot. |
| `locks` | `Locks` | `POST` | Relayed, then invalidates the snapshot on success. |
| `locks/verify` | `LocksVerify` | `POST` | Relayed verbatim. |
| `locks/batch` | `LocksBatch` | `POST` | Terminated. Proxy extension. |
| `locks/{id}/unlock` | `LocksUnlock` | `POST` | Relayed, then invalidates the snapshot on success. |

Any other method on any of these falls through to `Relay`, matching the handler's existing posture that a recognized path reached with an unexpected method is relayed rather than rejected.

`LfsRoute` gains a `LockId`, kept separate from `Oid` rather than reusing it: an object id is a SHA256 digest the proxy validates and addresses storage by, while a lock id is an opaque string the forge assigns and the proxy only passes back. Nothing validates the shape of a lock id, because the specification does not define one.

Classification is separable from termination, and should be landed that way. Adding the kinds while leaving the handler's `default` case to relay them is not a behaviour change, because relaying works from `RelayPath`, which classification does not alter. Tests should pin exactly that, so the step that classifies these routes cannot quietly become the step that breaks them.

## The snapshot

A `LockSnapshot` is an immutable list of locks for one `(upstream, repositoryPath, ref)` triple plus the instant it was assembled. `ref` is part of the key because the specification states the locking API's `ref` property is for authentication only, which means two callers presenting different refs may legitimately receive different answers and must not share an entry.

`ILockSnapshotStore` holds at most one published snapshot per key and swaps on publish. Nothing is persisted: a restart costs one refresh.

### Refresh

A request finding no snapshot, or one older than `Locks:ListTtl`, becomes the leader. The leader walks upstream cursors to exhaustion with the requesting client's `Authorization` header, assembles a snapshot, and publishes it. Concurrent requests for the same key wait on the leader and then read the published snapshot. This is the same leader-and-followers shape `FetchCoalescer` implements for objects, over a different key type, and the existing implementation should be generalised rather than copied.

A leader that fails relays its upstream failure to itself and to every follower, and no snapshot is published. A follower whose wait exceeds `Locks:RefreshTimeout` becomes a leader itself.

The refresh is what makes the deployment placement matter. A cursor walk of tens of pages costs seconds at residential round trip times and milliseconds from a host adjacent to the forge, and only one client per interval pays it either way.

### Serving

`GET locks` supports `path`, `id`, `limit` and `cursor`. `path` and `id` are exact-match filters, so applying them to the snapshot reproduces upstream's answer exactly, and the full snapshot is a superset of any filtered result by construction. `limit` and `cursor` are answered by slicing the snapshot and minting an opaque cursor that encodes the snapshot identity and an offset. A cursor referring to a snapshot that has since been replaced is answered from the current snapshot at offset zero with a fresh cursor, because a client walking a replaced snapshot would otherwise see a torn view.

Serving pages from one immutable snapshot is strictly better than relaying: a client walking upstream cursors today can observe locks appearing and disappearing mid-walk.

### Invalidation

A `LockCreate` or `LockDelete` that upstream answers with success marks the snapshot for that repository stale immediately, so the next read refreshes. Locks taken outside the proxy, by the forge web UI or a client not configured through it, are covered only by `ListTtl`. That is the reason `ListTtl` is short and not a tuning knob to be turned up casually.

## Credential admission

The snapshot cannot be served to whoever asks. Upstream requires authorization for `GET /locks`, and a cache that skips that check is an authorization bypass for anyone who can route to the proxy.

`ICredentialAdmission` holds a set of `(salted hash of the Authorization header, upstream, repositoryPath)` entries with an expiry of `Locks:AdmissionTtl`. The salt is generated per process and never leaves it. Admission is granted only by an upstream success:

- A client that leads a refresh proves its own authorization by that refresh succeeding, and is admitted as a side effect.
- A client arriving with a fresh snapshot already published and no admission entry is probed with a single `GET locks?limit=1` upstream under its own credential. Success admits it; any non-success is relayed verbatim and nothing is served from the snapshot.

So the steady state for a mid-sized studio is one cursor walk per `ListTtl` plus one single-page probe per artist per `AdmissionTtl`, against tens of round trips per artist per 30 seconds today.

The residual exposure is that a credential revoked upstream continues to read lock listings for up to `AdmissionTtl`. It is bounded, tunable, and grants only advisory metadata, never object bytes. For comparison the object plane's href token defaults to an hour.

## Fan-out, `POST locks/batch`

A proxy extension, not a specification endpoint. It is viable because the plugin vendors its own git-lfs binary (`GIT_USE_CUSTOM_LFS` is 1 at `GitSourceControlUtils.cpp:838`, with `git-lfs.exe` committed at the repository root), so the client half is a patch to a binary they already build and ship.

Request:

```json
{
  "operation": "lock",
  "ref": { "name": "refs/heads/feature/x" },
  "paths": ["Content/A.uasset", "Content/B.uasset"]
}
```

`operation` is `lock` or `unlock`. `unlock` accepts `ids` or `paths`, and `force` as a boolean. Response is always 200 when the request itself was well formed and the caller was admitted, because partial success is the normal outcome and a transport-level failure would discard the successful half:

```json
{
  "results": [
    { "path": "Content/A.uasset", "ok": true,  "lock": { "id": "871", "path": "Content/A.uasset", "locked_at": "2026-08-19T09:47:00Z", "owner": { "name": "someone" } } },
    { "path": "Content/B.uasset", "ok": false, "status": 409, "message": "already locked", "lock": { "owner": { "name": "someone else" } } }
  ]
}
```

Each item is one upstream `POST locks` or `POST locks/{id}/unlock` carrying the caller's `Authorization` header. Upstream decides every one of them individually, under the caller's own identity, which is what keeps this a parallelizer rather than a lock authority.

Unlock by path resolves ids from the current snapshot. A resolution that upstream then answers 404 or 409 triggers one snapshot refresh and one retry for that item only, because a stale snapshot can name a lock id that has since been released and reissued.

### Concurrency and rate limits

Fan-out runs under a limiter keyed by upstream and shared across every in-flight request in the process, bounded by `Locks:MaxFanOutConcurrency`. Per-request limiting would let two clients each batching five hundred paths collectively exceed what the forge tolerates.

GitHub secondary rate limits and Azure DevOps throttling are the expected failure, not an edge case. A 429 or 403 carrying `Retry-After` pauses the limiter for the whole upstream for that duration and the item is retried, up to `Locks:MaxFanOutRetries`. Items exhausting retries return `ok: false` with the upstream status. This behaviour is load bearing and belongs in the first implementation, not a follow-up.

`Locks:MaxFanOutPaths` rejects an oversized request with 413 rather than accepting work that will certainly be throttled.

### What the client must accept

The operation is not atomic and cannot be made atomic over an API with no transaction. A client that disconnects mid-fan-out leaves the already-granted locks granted. The plugin must treat the result array as authoritative and reconcile, which is the same property serial locking already has, made visible.

## Metadata-only mode

`Store:Enabled: false` runs the same binary with the object plane off, for a deployment adjacent to the forge that caches locks for clients whose object bytes should come from somewhere else.

- No object store, no eviction sweep, no staging timer, and `/readyz` no longer gates on a writable store root.
- `Batch` is handled as `Relay`. It is not rewritten at all, so upstream hrefs and their `header` maps reach the client untouched and object transfers bypass the proxy entirely. Routing batch through the existing relay rather than adding a pass-through mode to `BatchRewriter` keeps the rewriter with exactly one behaviour.
- `Transfer` and `Verify` become `Relay` as well, so a client holding a previously issued token is still served correctly by a full-mode replica sharing the same `TokenKeys`.
- Startup validation drops the writable-store-root requirement and gains a requirement that `Locks:Enabled` is true, because a metadata-only deployment with locks disabled does nothing.

Two deployments sharing `TokenKeys` and `PublicBaseUrl` are interchangeable from a client's point of view, which is what allows one hostname to resolve to a different instance for different clients without a client needing two credential entries.

## Repository allow-listing

`Upstreams` already allow-lists hosts. `UpstreamRegistry` resolves only configured keys, refuses a key containing a slash, and the handler answers 404 on an unresolved key before anything else happens. There is no wildcard and no default, so the proxy can never reach a host an operator did not name.

That allow-list is host-level. Any repository path under a configured upstream is proxyable, so anyone who can reach the proxy can make it cache objects from any repository they can read, spending the byte budget and evicting the working set.

`Repositories` is therefore **required** on every upstream, with no default and no empty-means-everything behaviour. A deployment configured without it refuses to start.

```json
"Upstreams": {
  "github": {
    "BaseUrl": "https://github.com",
    "Repositories": ["studio/**"]
  }
}
```

A pattern matches the **whole path following the upstream key**, not just the repository part of it, with `*` matching within a segment and `**` across segments. That path always continues into `info/lfs/objects/batch` or similar, so a repository entry normally ends in `**`: `studio/**` allows every route under every repository beginning `studio/`, while `studio/*` would allow only one further segment and therefore no real Git LFS path at all.

Matching the whole path rather than a repository prefix avoids having to decide where a repository path ends, which varies by forge (`owner/repo.git/info/lfs` against `org/project/_git/repo/info/lfs`) and which the route parser deliberately does not try to determine for a relay.

An operator who genuinely wants every repository writes `**` and has said so on purpose, which is the point: the permissive configuration stays available and stops being the one you get by not deciding.

This is a resource control, not an access control, and the distinction should stay visible in the code: upstream still authorizes every call with the client's own credential, and this list never grants access to anything. It exists so a shared cache with a fixed volume cannot have its working set displaced by traffic it was not deployed for.

Enforcement sits immediately after `TryResolve` in the handler and before any upstream call, so one check covers batch, transfer, verify, relay, and every route this subsystem adds. A non-matching path is 404, matching the treatment of an unknown upstream key, because distinguishing the two tells a caller which repositories exist.

For the locks routes specifically the list also bounds snapshot memory, since the number of snapshots a deployment can be made to hold is otherwise the number of repositories on the forge.

Refusing at startup rather than defaulting is the shape this codebase already uses. The validator refuses to run on a store root it cannot write, on the argument that a cache proxy whose volume is missing looks healthy from outside and then fails every transfer. A proxy silently caching whatever it is pointed at is the same class of problem: it looks healthy, and the symptom appears later as a hit ratio nobody can explain.

## Path containment

Related, and worth stating because this subsystem adds routes that reach upstream: a dot segment in a request path is refused by `LfsRouteParser`, because the upstream URL is built by joining the request path to the configured base URL and handing the result to `Uri`, which removes dot segments. Without the guard, a path could climb above a base URL carrying a path prefix, and `https://dev.azure.com/myorg` is the documented Azure DevOps shape, so that prefix is a tenancy boundary in practice.

The host can never change however the path is written, because the combined string always begins with the configured scheme and authority. Any new route added here inherits the guard for free by going through the same parser, which is the reason to keep it in the parser rather than at each call site.

## Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `IRepositoryAllowList` | Matches a repository path against an upstream's patterns | configuration |
| `LockSnapshot` | Immutable locks plus assembly instant for one key | none |
| `ILockSnapshotStore` | Publish and read the current snapshot per key | none |
| `LockListRefresher` | Walks upstream cursors under a caller credential, builds a snapshot | `IUpstreamClient` |
| `ICredentialAdmission` | Salted-hash admission set with expiry | `TimeProvider` |
| `LockCursorCodec` | Mints and reads snapshot-scoped cursors | `IHrefTokenCodec` primitives |
| `LockFanOut` | Bounded-concurrency lock and unlock with `Retry-After` handling | `IUpstreamClient`, `ILockSnapshotStore` |
| `IUpstreamLimiter` | Per-upstream concurrency and throttle backoff, process wide | `TimeProvider` |
| generalised coalescer | One refresh in flight per key, followers wait | existing `FetchCoalescer` |

`LockSnapshot` filtering, `LockCursorCodec`, and the admission expiry logic are pure and carry the densest tests.

## Configuration

```json
{
  "GitLfsCache": {
    "Store": { "Enabled": true },
    "Locks": {
      "Enabled": true,
      "ListTtl": "00:00:15",
      "AdmissionTtl": "00:01:00",
      "RefreshTimeout": "00:00:30",
      "MaxSnapshotLocks": 100000,
      "MaxFanOutConcurrency": 8,
      "MaxFanOutPaths": 1000,
      "MaxFanOutRetries": 3
    },
    "Upstreams": {
      "github": { "BaseUrl": "https://github.com", "Repositories": ["studio/**"] }
    }
  }
}
```

Startup validation rejects a `ListTtl` above `AdmissionTtl` (which would serve data older than the authorization proving it may be read), a `MaxFanOutConcurrency` below 1, `Store:Enabled: false` together with `Locks:Enabled: false`, an upstream whose `Repositories` list is missing or empty, and a `Repositories` entry that is not a valid pattern.

## Failure handling

- Any upstream non-success during a refresh, a probe, or a relayed lock call is returned verbatim, status and body, exactly as the batch path already does.
- A refresh failure publishes nothing. The previous snapshot, if any, keeps serving until `ListTtl` plus `RefreshTimeout`, after which requests block on a new leader rather than serving unbounded stale data.
- A snapshot exceeding `MaxSnapshotLocks` is not published and the subsystem falls back to relaying `GET locks` for that repository, logging once. An unbounded in-memory list is a worse failure than a slow client.
- Unadmitted credentials are never served from a snapshot under any failure path, including when upstream is unreachable. A cache that opens up when its authority is down is the wrong failure direction.

## Instrumentation

New counters on the existing `ktsu.GitLfsCache` meter: lock list hits, lock list refreshes, refresh failures, admission probes, admission rejections, fan-out items attempted, fan-out items succeeded, fan-out throttle pauses, and snapshot size. The ratio to watch is lock list hits against refreshes, which is directly the multiple by which upstream lock traffic has been reduced.

## Risks

- **Admission window.** A revoked credential reads listings for up to `AdmissionTtl`. Accepted, bounded, documented above.
- **Snapshot staleness.** Two clients can both see a file unlocked within `ListTtl`. Both then attempt the lock, upstream grants one and refuses the other, and the cost is a retry rather than a conflict. This is the argument that makes read caching safe and it should survive review intact.
- **Replica dilution.** Each replica holds its own snapshot and admission set, so N replicas produce N refreshes per interval. Acceptable at the small replica counts the deployment guidance already recommends, and worth measuring before scaling out.
- **Route collision.** If the Git LFS specification ever defines `POST /locks/batch`, the proxy's extension would shadow it. The fallback is to move the extension behind a proxy-owned prefix; the vendored client makes that a one-line change on both sides.
- **Forge throttling under fan-out.** Mitigated by the shared limiter, but the correct concurrency for GitHub and for Azure DevOps is not known in advance and must be found by measurement.

## Testing strategy

- `LockSnapshot` filtering against the specification's `path` and `id` semantics, including the single-result case the plugin's `check(Responses.Num() == 1)` at `GitSourceControlUtils.cpp:1563` depends on.
- Cursor minting and reading, including a cursor presented after its snapshot was replaced.
- Admission: a probe failure must never serve, an expired entry must re-probe, and two different credentials must not share an entry.
- Coalescing: one leader and many followers produce exactly one upstream cursor walk; a leader failure fails every follower; a follower timeout promotes a new leader.
- Fan-out: partial success shape, `Retry-After` honoured, limiter shared across simultaneous requests, oversized request rejected.
- Metadata-only mode: batch, transfer, and verify all relay, and no store is constructed.
- Repository allow-listing: a missing or empty list refuses startup, `**` allows everything, a non-matching path is 404 on every route kind including the new ones and produces no upstream call, and a matching path is unaffected.

## Deferred

- Sharing snapshots between replicas, which would need a shared cache and would reintroduce the state the object plane deliberately avoids.
- Caching `verify` per identity, if push-time verification ever becomes hot.
- Serving followers from the leader's partially assembled snapshot rather than making them wait for publication, which mirrors the deferred tailing improvement on the object plane.
