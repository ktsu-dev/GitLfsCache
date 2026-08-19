// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Locks;

/// <summary>
/// Remembers, briefly, that upstream accepted a credential for a repository.
/// </summary>
/// <remarks>
/// A cached lock listing cannot be served to whoever asks for it. Upstream requires authorization to
/// read locks, and a cache that skips that check is an authorization bypass for anyone who can route
/// to the proxy. This is what lets the check happen once per credential per interval instead of once
/// per request, without ever inventing an answer: an entry is only ever created by an upstream call
/// that actually succeeded.
/// <para>
/// This is not a credential store. Nothing here can be used to authenticate to upstream, and no
/// credential is retained.
/// </para>
/// </remarks>
public interface ICredentialAdmission
{
	/// <summary>
	/// Reports whether this credential was recently accepted upstream for this repository.
	/// </summary>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="repositoryPath">The repository the credential was accepted for.</param>
	/// <param name="authorization">The client's Authorization header, exactly as sent.</param>
	/// <returns><see langword="true"/> when an unexpired admission exists.</returns>
	public bool IsAdmitted(string upstream, string repositoryPath, string? authorization);

	/// <summary>
	/// Records that upstream accepted this credential for this repository.
	/// </summary>
	/// <remarks>
	/// Only ever called after a real upstream success. Calling it anywhere else would turn the cache
	/// into the authority it is designed never to be.
	/// </remarks>
	/// <param name="upstream">The upstream key.</param>
	/// <param name="repositoryPath">The repository the credential was accepted for.</param>
	/// <param name="authorization">The client's Authorization header, exactly as sent.</param>
	public void Admit(string upstream, string repositoryPath, string? authorization);
}
