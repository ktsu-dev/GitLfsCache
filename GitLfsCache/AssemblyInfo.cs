// Copyright (c) 2023-2026 ktsu.dev contributors

using System.Runtime.CompilerServices;

// The endpoint handlers, store internals, and token codec details are internal. The test project
// exercises them directly rather than through a public surface that exists only for testing.
[assembly: InternalsVisibleTo("ktsu.GitLfsCache.Tests")]
