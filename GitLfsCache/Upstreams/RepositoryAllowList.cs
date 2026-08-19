// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Upstreams;

using System.Text.RegularExpressions;
using ktsu.GitLfsCache.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Matches repository paths against the patterns configured for each upstream.
/// </summary>
/// <remarks>
/// This is a resource control and not an access control. Upstream still authorizes every call with
/// the client's own credential, and nothing here grants access to anything. It exists so a shared
/// cache with a fixed byte budget cannot have its working set displaced by traffic it was not
/// deployed for.
/// <para>
/// Patterns are translated to anchored regular expressions once in the constructor. Matching is case
/// insensitive because forge repository names are, and a pattern that fails only because someone
/// typed <c>Studio</c> is a support ticket rather than a control.
/// </para>
/// </remarks>
/// <param name="options">The configured options.</param>
public sealed class RepositoryAllowList(IOptions<GitLfsCacheOptions> options) : IRepositoryAllowList
{
	/// <summary>
	/// How long a single match may run before it is abandoned.
	/// </summary>
	/// <remarks>
	/// The translated patterns have no backtracking construct, so this cannot fire in practice. It is
	/// set because a regular expression built from configuration and run against a request path is
	/// exactly the shape that should carry a timeout.
	/// </remarks>
	private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

	private readonly Dictionary<string, Regex[]> _patterns = options.Value.Upstreams
		.ToDictionary(
			pair => pair.Key,
			pair => pair.Value.Repositories
				.Where(pattern => !string.IsNullOrWhiteSpace(pattern))
				.Select(Translate)
				.ToArray(),
			StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public bool IsAllowed(string upstream, string path)
	{
		Ensure.NotNull(upstream);
		Ensure.NotNull(path);

		if (!_patterns.TryGetValue(upstream, out Regex[]? patterns))
		{
			return false;
		}

		string candidate = path.Trim('/');

		foreach (Regex pattern in patterns)
		{
			if (pattern.IsMatch(candidate))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Translates a glob pattern to an anchored regular expression.
	/// </summary>
	/// <remarks>
	/// The pattern is escaped first so every character is literal, then the two wildcards are put
	/// back. Order matters: <c>**</c> is restored before <c>*</c>, otherwise the first star of a
	/// double would be consumed as a single. Escaping produces <c>\*</c>, and the replacement for
	/// <c>**</c> contains no backslash, so the second replacement cannot reach inside it.
	/// </remarks>
	/// <param name="pattern">The configured pattern.</param>
	/// <returns>The compiled expression.</returns>
	private static Regex Translate(string pattern)
	{
		string escaped = Regex.Escape(pattern.Trim('/'));

		string expression = escaped
			.Replace(@"\*\*", ".*", StringComparison.Ordinal)
			.Replace(@"\*", "[^/]*", StringComparison.Ordinal);

		return new Regex(
			$"^{expression}$",
			RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
			MatchTimeout);
	}
}
