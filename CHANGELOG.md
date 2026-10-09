## v1.12.3 (patch)

Changes since v1.12.2:

- Count follower attempts in a while loop so its condition and counter agree ([@Claude](https://github.com/Claude))
- Merge remote-tracking branch 'origin/main' into fix/62-range-on-cache-hit ([@Claude](https://github.com/Claude))
- Serve a Range request for a cached object as 206 instead of the whole object [patch] ([@Claude](https://github.com/Claude))

## v1.12.2 (patch)

Changes since v1.12.1:

- Hand an aborted leader's fetch to one follower instead of all of them [patch] ([@Claude](https://github.com/Claude))
- Store objects for an upstream key with a dot in it, such as gitlab.com [patch] ([@Claude](https://github.com/Claude))
- Expire a batch action's proxy token no later than upstream's href [patch] ([@Claude](https://github.com/Claude))
- Cover the --store override inside the command action ([@Claude](https://github.com/Claude))
- Answer 502, not 500, when upstream's 2xx batch body cannot be used [patch] ([@Claude](https://github.com/Claude))
- Resolve a relative --store path instead of aborting startup [patch] ([@Claude](https://github.com/Claude))
- Refuse a locks/batch ref that is not an object with 400 instead of 500 [patch] ([@Claude](https://github.com/Claude))

## v1.12.1 (patch)

Changes since v1.12.0:

- Lift the request body limit on the transfer route so large pushes go through [patch] ([@Claude](https://github.com/Claude))

## v1.12.1-pre.1 (prerelease)

Changes since v1.12.0:

- Bump MSTest.Sdk from 4.4.1 to 4.5.1 ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.12.0 (minor)

Changes since v1.11.0:

- Evict stale and excess lock snapshots [minor] ([@Claude](https://github.com/Claude))

## v1.11.1-pre.1 (prerelease)

Changes since v1.11.0:

- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.11.0 (minor)

Changes since v1.10.0:

- Key the cache by the configured upstream spelling, not the request's [minor] ([@Claude](https://github.com/Claude))
- Move CI onto the shared ci-shared.yml pipeline ([@Claude](https://github.com/Claude))

## v1.10.2-pre.2 (prerelease)

Changes since v1.10.2-pre.1:

- Bump Polyfill from 11.4.1 to 11.4.2 ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.10.2-pre.1 (prerelease)

Changes since v1.10.1:

- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.10.1 (patch)

Changes since v1.10.0:

- Move CI onto the shared ci-shared.yml pipeline ([@Claude](https://github.com/Claude))

## v1.10.0 (minor)

Changes since v1.9.0:

- Relay transfers uncached when no staging file can be opened ([@Claude](https://github.com/Claude))
- Make --allow replace an upstream's configured allow-list ([@Claude](https://github.com/Claude))
- Merge main into fix/lock-list-forwards-refspec ([@Claude](https://github.com/Claude))
- Merge remote-tracking branch 'origin/main' into fix/lock-list-forwards-refspec ([@Claude](https://github.com/Claude))
- Forward the client's refspec on cached lock walks and probes ([@Claude](https://github.com/Claude))

## v1.9.0 (minor)

Changes since v1.8.0:

- Filter the invalidated keys with Where ([@Claude](https://github.com/Claude))
- Discard the ignored Position value in FailingWriteStream ([@Claude](https://github.com/Claude))
- Invalidate every ref's lock snapshot when a relayed lock changes ([@Claude](https://github.com/Claude))
- Refuse to publish a staging file whose write failed ([@Claude](https://github.com/Claude))
- Gate Dependabot auto-merge on CI actually being green ([@Claude](https://github.com/Claude))
- refactor: split the lock routes into their own handler [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- fix: treat a lost publish race as the duplicate it is [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: adopt the consolidated .NET workflow [patch] ([@Claude](https://github.com/Claude))
- fix: guard staging files in the store rather than relying on the host [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: make the SonarQube quality gate opt in [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: adopt the unified dotnet workflow [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Reduce complexity in the tool entry point and the fan-out parser ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Clear the Sonar findings from the locks work ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.8.31 (patch)

Changes since v1.8.30:

- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.30 (patch)

Changes since v1.8.29:

- Bump the ktsu group with 7 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.29 (patch)

Changes since v1.8.28:

- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.28 (patch)

Changes since v1.8.27:

- Bump the ktsu group with 4 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.27 (patch)

Changes since v1.8.26:

- Bump Polyfill from 11.3.0 to 11.4.0 ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.26 (patch)

Changes since v1.8.25:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.25 (patch)

Changes since v1.8.24:

- Bump MSTest.Sdk from 4.4.0 to 4.4.1 ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.24 (patch)

Changes since v1.8.23:

- Bump the ktsu group with 7 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.23 (patch)

Changes since v1.8.22:

- Gate Dependabot auto-merge on CI actually being green ([@Claude](https://github.com/Claude))
- refactor: split the lock routes into their own handler [patch] ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.8.22 (patch)

Changes since v1.8.21:

- fix: treat a lost publish race as the duplicate it is [patch] ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.8.21 (patch)

Changes since v1.8.20:

- Bump the ktsu group with 7 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.20 (patch)

Changes since v1.8.19:

- ci: adopt the consolidated .NET workflow [patch] ([@Claude](https://github.com/Claude))

## v1.8.19 (patch)

Changes since v1.8.18:

- Bump Polyfill from 11.2.0 to 11.3.0 ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.18 (patch)

Changes since v1.8.17:

- Bump the microsoft group with 1 update ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.17 (patch)

Changes since v1.8.16:

- Bump the system group with 1 update ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the microsoft group with 1 update ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.16 (patch)

Changes since v1.8.15:

- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.15 (patch)

Changes since v1.8.14:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.14 (patch)

Changes since v1.8.13:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.13 (patch)

Changes since v1.8.12:

- Bump MSTest.Sdk from 4.3.3 to 4.4.0 ([@dependabot[bot]](https://github.com/dependabot[bot]))
- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.12 (patch)

Changes since v1.8.11:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.11 (patch)

Changes since v1.8.10:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.10 (patch)

No significant changes detected since v1.8.9.

## v1.8.9 (patch)

Changes since v1.8.8:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.8 (patch)

Changes since v1.8.7:

- Bump the ktsu group with 3 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.7 (patch)

Changes since v1.8.6:

- fix: guard staging files in the store rather than relying on the host [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: make the SonarQube quality gate opt in [patch] ([@matt-edmondson](https://github.com/matt-edmondson))
- ci: adopt the unified dotnet workflow [patch] ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.8.6 (patch)

Changes since v1.8.5:

- Bump the ktsu group with 2 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.5 (patch)

No significant changes detected since v1.8.4.

## v1.8.4 (patch)

Changes since v1.8.3:

- Bump the ktsu group with 5 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.3 (patch)

Changes since v1.8.2:

- Bump the ktsu group with 7 updates ([@dependabot[bot]](https://github.com/dependabot[bot]))

## v1.8.2 (patch)

Changes since v1.8.1:

- [patch] Reduce complexity in the tool entry point and the fan-out parser ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.8.1 (patch)

Changes since v1.8.0:

- [patch] Clear the Sonar findings from the locks work ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.8.0 (minor)

Changes since v1.7.0:

- docs: scope build badge to the default branch ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add metadata-only mode ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Document the locks subsystem ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add batched locking as a proxy extension ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Cache the Git LFS lock listing and require a repository allow-list ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Add designs for the locks subsystem and a branch state cache ([@matt-edmondson](https://github.com/matt-edmondson))
- docs: correct README, DESCRIPTION and TAGS metadata ([@matt-edmondson](https://github.com/matt-edmondson))
- Add mailmap ([@matt-edmondson](https://github.com/matt-edmondson))
- Add mailmap ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.7.1 (patch)

Changes since v1.7.0:

- Add mailmap ([@matt-edmondson](https://github.com/matt-edmondson))
- Add mailmap ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.7.0 (minor)

Changes since v1.6.0:

- [minor] Split the container host into GitLfsCache.Service ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Set PackageProjectUrl explicitly so packaging and container publish work ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Mark the implementation plan complete and correct its wrong assumptions ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Address the SonarQube findings from CI ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Write the README and record the as-built deviations in the spec ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.6.2 (patch)

Changes since v1.6.1:

- [patch] Set PackageProjectUrl explicitly so packaging and container publish work ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.6.1 (patch)

Changes since v1.6.0:

- [patch] Mark the implementation plan complete and correct its wrong assumptions ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Address the SonarQube findings from CI ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Write the README and record the as-built deviations in the spec ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.6.0 (minor)

Changes since v1.5.0:

- [minor] Add kustomize base and container publish job ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add the gitlfscache tool host with friendly flags ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.5.0 (minor)

Changes since v1.4.0:

- [minor] Add endpoints, DI wiring, metrics, and end-to-end integration tests ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.4.0 (minor)

Changes since v1.3.0:

- [minor] Add fetch coalescer so one upstream fetch serves concurrent misses ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add upstream client and pure request builders ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.3.0 (minor)

Changes since v1.2.0:

- [minor] Add least-recently-used eviction, store maintenance, and startup checks ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.2.0 (minor)

Changes since v1.1.0:

- [minor] Add content-addressed object store with verify-before-publish ([@matt-edmondson](https://github.com/matt-edmondson))

## v1.1.0 (major)

- [minor] Add stream tee and hashing stream for single-pass object verification ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add batch response rewriter preserving unknown properties ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add authenticated href token codec with key rotation ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Align file headers with the generated COPYRIGHT.md ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Add size parser, configuration options, startup validation, and upstream registry ([@matt-edmondson](https://github.com/matt-edmondson))
- [minor] Scaffold GitLfsCache solution, tool package, and container publish ([@matt-edmondson](https://github.com/matt-edmondson))
- [pre] Add implementation plan through the object store task ([@matt-edmondson](https://github.com/matt-edmondson))
- [pre] Add ktsu.GitLfsCache design spec ([@matt-edmondson](https://github.com/matt-edmondson))

