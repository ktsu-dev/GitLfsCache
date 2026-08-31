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

