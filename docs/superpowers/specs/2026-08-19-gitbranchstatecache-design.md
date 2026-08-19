# ktsu.GitBranchStateCache Design

Date: 2026-08-19
Status: proposed. Belongs in its own repository; the spec is co-located here because it originates from the same investigation as the locks subsystem.

## Purpose

Answer one question quickly, for many clients, over a wide-area link: *for these paths, which of these branches carry changes I do not have, and what content do they carry?*

The driving workload is `CheckRemote` in the Unreal Engine source control plugin (`GitSourceControlUtils.cpp:1358`). Today every open editor, every 30 seconds, runs a `git fetch` and then one `git log` plus one `git diff` per monitored branch, and intersects the two result sets to decide which assets are out of date and must not be locked. That is O(branches) process spawns and a network fetch on a fixed heartbeat, on every workstation, and it is worst for the people on residential links.

This service does that work once, centrally, adjacent to the forge, and serves the result. The client keeps a final comparison it can only do locally.

## Non-goals

- Not a version control system, and not a git server. It never serves object content, never accepts writes, and is never a source of truth for anything.
- No authorization decisions of its own. A client is served only after its own credential has been proven against the forge.
- No service credentials. Every upstream operation is performed with a requesting client's credential, exactly as `ktsu.GitLfsCache` does.
- Not forge-specific. See below: the design deliberately avoids every forge REST API.
- Does not know or care about a client's working tree, uncommitted edits, or local commits.

## Why not the forge REST APIs

The obvious implementation is GitHub's compare endpoint and Azure DevOps' commit diff endpoint. GitHub rules itself out:

> The list of changed files is only shown on the first page of results, and it includes up to 300 changed files for the entire comparison.

A branch divergence in an Unreal project exceeds 300 files routinely, and the truncation is silent from the client's point of view: a partial answer looks exactly like a complete one, and the failure mode is failing to warn someone that an asset is stale. Azure DevOps' equivalent paginates properly, but building on it would mean a per-forge adapter, a per-forge correctness story, and a per-forge set of rate limits, for a shop that uses both.

Instead the service uses **git over HTTPS and nothing else**. Every operation it performs (`ls-remote`, `fetch`, `merge-base`, `diff`) is protocol-level, works identically against GitHub and Azure DevOps, has no file count cap, and returns exact answers. There is no forge adapter layer, because there is nothing forge-specific left to adapt.

## Decisions

| Decision | Chosen | Rejected alternative |
|---|---|---|
| Diff source | A bare, blobless mirror per repository | Forge compare APIs, which cap at 300 files on GitHub and need a per-forge adapter |
| Mirror shape | `--bare --filter=blob:none` | A full mirror, which fetches blob content the diff never reads |
| What the client sends | Its latest *pushed* ancestor, not its true HEAD | True HEAD, which the mirror may not contain and cannot compute a merge base against |
| What the service returns | Blob ids per path per branch | A boolean "is stale", which forces the service to know the client's working tree |
| Diff cache key | `(mergeBase, branchTip)` | `(head, branchTip)`, which barely deduplicates because every client's head differs |
| Fetch trigger | A client request, leader-pays and coalesced | A background timer, which needs a service credential |
| Authorization probe | `git ls-remote` under the caller's credential | A forge REST call, which reintroduces the per-forge adapter the design just removed |
| Repository allow-list | Required per upstream, no default | Optional with empty meaning everything, which here would let one request create a permanent clone |
| Concurrency safety | Every read names explicit object ids, never ref names | Reading through refs, which races with an in-flight fetch |

## The division of labour

The service works entirely in **pushed-commit space**. The client works in **working-tree space**. Neither needs to know about the other's half, and that split is what keeps the service stateless with respect to its users.

The client sends the latest ancestor of its HEAD that has been pushed, obtained locally with no network access (`git rev-parse @{upstream}` or `git merge-base HEAD @{upstream}`). Local unpushed commits are deliberately excluded, because they can only make the client *more* current than the base it declares, and the final comparison catches that anyway.

The service answers with, for each requested path and each queried branch, the blob id that path has at that branch's tip, but only where it differs from the merge base. The client then compares that blob id against its own (`git ls-files -s`, or `git rev-parse HEAD:<path>`), which is local, exact, and cheap.

That comparison is strictly better than what the plugin does today. The current intersection of `git log --name-only` and `git diff --name-only` is an approximation that exists to catch the case where a file was changed and then changed back. Comparing blob ids catches that case and every other case, exactly, because two identical files always have the same blob id.

It is also exactly right for LFS-tracked assets with no special handling. A `.uasset` under LFS is stored in git as a small pointer file naming the object id, so comparing pointer blob ids is comparing LFS object ids. The service never needs to know which files are LFS-tracked.

## API

All routes are under an upstream key as the first path segment, matching `ktsu.GitLfsCache`'s addressing so one deployment serves many forges.

### `POST /v1/{upstream}/{repositoryPath}/state`

```json
{
  "base": "a1b2c3...",
  "branchPatterns": ["origin/main", "origin/release/*"],
  "paths": ["Content/Maps/Foo.umap", "Content/Chars/Bar.uasset"]
}
```

`paths` is optional; omitting it returns every changed path, which is what a client warming its whole state wants. `branchPatterns` accepts the same wildcard forms the plugin already keeps in `StatusBranchNamePatternsInternal`, so the caller does not have to enumerate branches itself.

```json
{
  "base": "a1b2c3...",
  "branches": [
    { "name": "origin/main", "tip": "d4e5f6...", "mergeBase": "a1b2c3..." }
  ],
  "paths": {
    "Content/Chars/Bar.uasset": [
      { "branch": "origin/main", "blob": "9f8e7d...", "status": "M" }
    ]
  },
  "refsAsOf": "2026-08-19T09:47:00Z"
}
```

A path absent from `paths` is unchanged on every queried branch relative to its merge base. `status` carries git's raw status letter so a client can distinguish a delete from a modification.

`409 unknown-base` when `base` is not an object the mirror contains, even after a fetch. The body carries the current branch tips so the client can decide what to do; the plugin should fall back to its existing local computation for that cycle rather than treating the state as unknown.

### `GET /v1/{upstream}/{repositoryPath}/branches?pattern=`

Resolves wildcard patterns against the mirror's remote refs and returns names with tip ids. This removes the other reason the plugin currently needs a network fetch on its heartbeat: `GetRemoteBranchesWildcard` needs an up-to-date ref list, and nothing else.

### `GET /healthz`, `GET /readyz`

Liveness, and readiness gated on a writable mirror root and valid configuration, matching `ktsu.GitLfsCache`.

## Request flow

1. Resolve the upstream key to a base URL. Unknown key is 404.
2. Check credential admission for `(credential, upstream, repository)`. On a miss, run `git ls-remote --heads` against the upstream with the caller's credential. Success admits for `AdmissionTtl`; failure is returned to the client and nothing else happens. `ls-remote` is the whole authorization mechanism: it is forge-agnostic, cheap, and proves precisely the read access being requested.
3. If the mirror's refs are older than `RefsTtl`, become the leader and run an incremental `git fetch` with the caller's credential, coalesced so concurrent requests wait rather than piling on. A follower exceeding `FetchTimeout` proceeds against the refs it has.
4. Resolve `branchPatterns` to concrete refs and read their tip ids **once**, into local variables. Everything downstream names those ids explicitly.
5. For each branch, `git merge-base <base> <tip>`.
6. For each `(mergeBase, tip)` pair, read the changed-path set from the diff cache, or compute it with `git diff --raw <mergeBase> <tip>` and store it.
7. Filter to the requested paths and project into the response.

Step 4 is the concurrency invariant: because every later step names object ids rather than ref names, a fetch landing mid-request cannot produce a torn answer. It also means the diff cache is keyed by immutable content and never needs invalidation, only eviction.

## Repository allow-listing

`Repositories` is **required** on every upstream, with no default and no empty-means-everything behaviour. A deployment configured without it refuses to start. This matches `ktsu.GitLfsCache`, and the two should stay aligned so an operator configuring both does not have to remember which one lets the setting be skipped.

What differs is the cost of getting it wrong, and that is worth stating because it sets how hard the escape hatch should be to reach. The object cache holds content it was asked for and evicts it under a byte budget, so an unexpected repository costs cache warmth. This service **clones a mirror**, and a mirror is created by a single request, sized by the repository rather than by the request, and never evicted. One request for an unlisted repository is a permanent clone of it onto a shared volume.

Credential admission does not cover this. Admission gates who may be *served*, and by the time it is checked the caller has already proven read access, which is precisely the case that would create the mirror. For that reason the `**` pattern that `ktsu.GitLfsCache` accepts as a deliberate "everything" is **rejected here**: on a service that mirrors, there is no legitimate configuration meaning "clone whatever anyone asks for". Patterns must name at least one literal path segment.

```json
"Upstreams": {
  "github": {
    "BaseUrl": "https://github.com",
    "Repositories": ["studio/game.git", "studio/tools-*"]
  }
}
```

Patterns match the repository path following the upstream key, with `*` matching within a segment and `**` across segments, subject to the literal-segment requirement above. Unlike `ktsu.GitLfsCache`, where the matched path continues into the Git LFS route and so normally needs a trailing `**`, this service's routes name the repository path exactly, so `studio/game.git` is a complete pattern. The check runs before admission and before any mirror is touched, and a non-matching path is 404.

Ordering matters here and should be tested explicitly: **allow-list first, then admission**. Reversed, an unlisted repository would still be probed against the forge with the caller's credential before being refused, which turns the service into an oracle for which repositories a credential can read.

As with the object cache this is a resource control and not an access control. It never grants anything, and a listed repository is still served only to a caller whose own credential passed `ls-remote`.

## The mirror

One bare repository per `(upstream, repository)`:

```
<root>/<upstream>/<sanitized repository path>/mirror.git
```

Created with `git clone --bare --filter=blob:none` plus an explicit mirror refspec, then maintained with `git fetch --prune`.

Two things make this much cheaper than it sounds. A partial clone with `blob:none` fetches commits and trees but no file content, and `git diff --raw` reports blob ids rather than blob contents, so it never triggers a lazy fetch of the filtered blobs. And in an LFS repository the large assets are pointer files of a hundred or so bytes each, so the git object store was never carrying the bulk anyway.

The correct behaviour on any git invocation that would demand a filtered blob is to fail the request rather than silently fetch. `GIT_NO_LAZY_FETCH=1` enforces that, and it should be set on every invocation so a mistake surfaces as an error in testing rather than as an enormous fetch in production.

### Credentials to git

The caller's credential is handed to git through a credential helper reading from the environment or standard input, never through `-c http.extraHeader=` or a URL. Command lines are visible to any process on the host, and this process handles many users' forge credentials.

### Cache and eviction

The diff cache is in memory, keyed by `(repository, mergeBase, tip)`, bounded by entry count with least-recently-used eviction. Keys are immutable so entries are never wrong, only cold.

The key choice matters more than it looks. Keying on the client's `base` would barely deduplicate, because every artist sits on a slightly different commit. Keying on the merge base collapses them: a whole team working off one integration point shares one merge base, so one computed diff per branch serves all of them.

## Components

| Unit | Responsibility | Depends on |
|---|---|---|
| `UpstreamRegistry` | Maps an upstream key to a base URL | configuration |
| `IRepositoryAllowList` | Matches a repository path against an upstream's required patterns | configuration |
| `IMirrorStore` | Locates, creates, and reports the age of a repository mirror | `IFileSystem`, `ktsu.Semantics.Paths` |
| `IGitRunner` | Runs one git invocation with a timeout, cancellation, and no credential on the command line | none |
| `MirrorFetcher` | Coalesced incremental fetch under a caller credential | `IGitRunner`, coalescer |
| `IRefResolver` | Expands wildcard patterns to concrete refs and tip ids | `IGitRunner` |
| `IDiffSource` | `(mergeBase, tip)` to changed paths with blob ids | `IGitRunner` |
| `DiffCache` | Bounded least-recently-used cache over `IDiffSource` | `IDiffSource` |
| `ICredentialAdmission` | Salted-hash admission set with expiry, proven by `ls-remote` | `IGitRunner`, `TimeProvider` |
| endpoint module | Maps state, branches, and health probes | all of the above |

`IRefResolver` pattern matching, the `git diff --raw` parser, and the cache eviction policy are pure and carry the densest tests. `IGitRunner` is the one unit that touches a process, and everything above it is testable with a fake.

## Configuration

```json
{
  "GitBranchStateCache": {
    "MirrorRoot": "/var/lib/gitbranchstatecache",
    "RefsTtl": "00:00:30",
    "AdmissionTtl": "00:01:00",
    "FetchTimeout": "00:02:00",
    "DiffTimeout": "00:02:00",
    "MaxCachedDiffs": 2000,
    "MaxPathsPerRequest": 20000,
    "Upstreams": {
      "github": {
        "BaseUrl": "https://github.com",
        "Repositories": ["studio/game.git"]
      },
      "ado": {
        "BaseUrl": "https://dev.azure.com/myorg",
        "Repositories": ["myproject/_git/game"]
      }
    }
  }
}
```

Startup validation refuses to run with no upstreams, an upstream with an empty or missing `Repositories` list, a pattern that is invalid or names no literal segment, an unwritable mirror root, a `RefsTtl` above `AdmissionTtl`, or a non-positive timeout, and reports every problem at once, matching `ktsu.GitLfsCache`'s behaviour and for the same reason: a cache whose volume is missing looks healthy from outside and then fails every request.

## Deployment

Ships as a container image and a dotnet tool from one binary, as `ktsu.GitLfsCache` does.

Deploy adjacent to the forge, not on-premises. Its cost is round trips and its clients are worst-served on residential links, which is the opposite placement from an object cache. A StatefulSet with a read-write-once volume for mirrors; the volume is modest because the mirrors are blobless and LFS keeps the bulk out of git.

Start at one replica. Each replica holds its own mirrors and diff cache, so replicas multiply fetch traffic and disk without improving hit rate, exactly as the object cache's guidance says.

## Failure handling

- An admission failure is returned with the forge's own status where one is available, and nothing is served.
- A fetch failure serves the refs already present and marks the response `refsAsOf` accordingly, so a client can see the data is old. A total inability to fetch on a repository with no mirror yet is a 503.
- A `git diff` exceeding `DiffTimeout` returns 504 for that branch only; other branches in the same request still answer. A partial answer is explicitly labelled so a client never mistakes it for "nothing changed".
- Any git invocation is killed on request cancellation, and the process tree is reaped. Leaking git processes is the most likely operational failure of a service shaped like this and deserves a test.
- A malformed `git diff --raw` line fails the request rather than being skipped. Silently dropping a line means silently failing to warn someone about a stale asset.

## Instrumentation

A `ktsu.GitBranchStateCache` meter: state requests, diff cache hits and misses, fetches, fetch failures, fetch duration, diff computation duration, admission probes and rejections, mirror bytes on disk, and paths returned per request. The ratio to watch is diff cache hits against misses, which reflects how well merge bases are clustering; a low ratio means the team is spread across many integration points and the cache bound may need raising.

## Risks

- **Mirror disk growth.** Bounded by the allow-list rather than by traffic, which is what makes the volume sizeable in advance. Still wants an idle-repository reaper, since an allow-listed repository that stops being queried keeps its mirror forever.
- **It holds a mirror of source.** A service with read-only copies of every repository is a more attractive target than a blob cache. Admission is the only control, and it must be impossible to bypass, including when the forge is unreachable.
- **Unknown base.** Clients that have not pushed in a long time, or that rewrote history, get `409 unknown-base` and fall back to local computation. Worth measuring how often this fires before relying on the service.
- **Enormous divergence.** A long-lived branch can produce a diff of a hundred thousand paths. The computation is cached, but the first one is slow and the response is large. `MaxPathsPerRequest` bounds the response; the client should send the paths it actually cares about rather than omitting the field.
- **Process management.** Spawning git per request is the least .NET-shaped part of this design and the most likely source of resource leaks under load.
- **Blobless clone assumptions.** If a future git operation in this service does need blob content, `GIT_NO_LAZY_FETCH` turns that into a visible error, but the design should be revisited rather than the flag removed.

## Testing strategy

- `git diff --raw` parsing against real output for modifications, additions, deletions, renames, and mode changes, including paths needing quoting and paths with non-ASCII characters.
- Wildcard pattern resolution against the hierarchy shapes the plugin uses, including a pattern matching nothing.
- Diff cache: two different `base` values resolving to one merge base must produce one computation.
- Admission: an `ls-remote` failure must never serve, including when a mirror already exists locally with the data.
- Allow-listing: a missing or empty `Repositories` list refuses startup, as does a pattern naming no literal segment; an unlisted repository is 404 and, critically, produces **no** upstream call at all, so the service cannot be used to probe which repositories a credential can read; no mirror directory is created for an unlisted path.
- Concurrency: a fetch landing mid-request must not change that request's answer, which is the explicit-object-id invariant made into a test.
- Cancellation: an aborted request kills its git process.
- Against a real local bare repository fixture for the git-touching units, and fakes above them.

## Deferred

- Forge webhooks to invalidate refs on push instead of `RefsTtl`, which would make answers fresh rather than merely recent, at the cost of inbound reachability and a shared secret.
- An Azure DevOps commit-diff fast path for repositories where it is adequate, avoiding the mirror entirely. Not worth a per-forge adapter until the mirror proves to be the bottleneck.
- Persisting the diff cache so a restart does not cost a stampede of recomputation.
- Serving the plugin a precomputed whole-project state on connect, rather than per-heartbeat queries.
