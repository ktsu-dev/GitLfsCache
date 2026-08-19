// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

using System.Net;

/// <summary>
/// What a listing walk produced.
/// </summary>
/// <remarks>
/// The three failure cases are distinguished because the correct response differs. A refusal is
/// upstream's answer and belongs to the client verbatim. A body that could not be understood, or a
/// repository past the lock ceiling, are both the proxy's problem, and the answer to both is to stop
/// caching this repository and relay instead.
/// </remarks>
/// <param name="Outcome">Which of the outcomes this is.</param>
/// <param name="Snapshot">The assembled snapshot, when the walk succeeded.</param>
/// <param name="Status">Upstream's status, when it refused.</param>
/// <param name="LockCount">How many locks were seen, when the repository was over the ceiling.</param>
public sealed record LockRefreshResult(
	LockRefreshOutcome Outcome,
	LockSnapshot? Snapshot,
	HttpStatusCode? Status,
	int LockCount)
{
	/// <summary>Builds a successful outcome.</summary>
	/// <param name="snapshot">The assembled snapshot.</param>
	/// <returns>The result.</returns>
	public static LockRefreshResult Succeeded(LockSnapshot snapshot) =>
		new(LockRefreshOutcome.Succeeded, snapshot, null, 0);

	/// <summary>Builds an upstream refusal.</summary>
	/// <param name="status">Upstream's status.</param>
	/// <returns>The result.</returns>
	public static LockRefreshResult Refused(HttpStatusCode status) =>
		new(LockRefreshOutcome.Refused, null, status, 0);

	/// <summary>Builds an outcome for a body that could not be understood.</summary>
	/// <returns>The result.</returns>
	public static LockRefreshResult Unusable() =>
		new(LockRefreshOutcome.Unusable, null, null, 0);

	/// <summary>Builds an outcome for a repository past the lock ceiling.</summary>
	/// <param name="lockCount">How many locks were seen before giving up.</param>
	/// <returns>The result.</returns>
	public static LockRefreshResult TooLarge(int lockCount) =>
		new(LockRefreshOutcome.TooLarge, null, null, lockCount);
}
