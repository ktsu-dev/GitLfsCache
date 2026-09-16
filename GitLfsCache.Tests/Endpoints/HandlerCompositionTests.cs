// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Endpoints;

using System.Reflection;
using ktsu.GitLfsCache.Endpoints;
using ktsu.GitLfsCache.Locks;
using ktsu.GitLfsCache.Storage;
using ktsu.GitLfsCache.Tokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Guards the split between the dispatcher and the two route handlers.
/// </summary>
/// <remarks>
/// Written against constructors rather than behaviour because what is being guarded is a dependency
/// boundary, and the behaviour either side of it is already covered by the integration suite. The
/// dispatcher acquired its lock dependencies one at a time, each reasonable on its own, until it held
/// fourteen; a test that fails the moment an unrelated dependency is added is the only thing that
/// notices that happening again.
/// </remarks>
[TestClass]
public class HandlerCompositionTests
{
	/// <summary>
	/// The constructor parameter budget SonarQube enforces (S107).
	/// </summary>
	private const int MaxConstructorParameters = 7;

	private static ParameterInfo[] ConstructorParametersOf<T>() =>
		typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
			.Single()
			.GetParameters();

	[TestMethod]
	public void Dispatcher_StaysWithinTheConstructorParameterBudget()
	{
		ParameterInfo[] parameters = ConstructorParametersOf<GitLfsCacheHandler>();

		Assert.IsLessThanOrEqualTo(
			MaxConstructorParameters,
			parameters.Length,
			$"{nameof(GitLfsCacheHandler)} dispatches; it does not do the work. It now takes "
			+ $"{parameters.Length} dependencies ({string.Join(", ", parameters.Select(p => p.Name))}). "
			+ "A new dependency here almost always belongs to one of the route handlers instead.");
	}

	[TestMethod]
	public void LockHandler_StaysWithinTheConstructorParameterBudget()
	{
		ParameterInfo[] parameters = ConstructorParametersOf<LockRouteHandler>();

		Assert.IsLessThanOrEqualTo(MaxConstructorParameters, parameters.Length);
	}

	/// <summary>
	/// The lock routes need the upstream and the allow-list check, both already done by the dispatcher
	/// before either handler is called. Nothing about transfer tokens or the object store reaches them,
	/// and taking either back would put the two concerns into one class again.
	/// </summary>
	[TestMethod]
	public void LockHandler_TakesNoObjectSideDependencies()
	{
		IEnumerable<Type> dependencies = ConstructorParametersOf<LockRouteHandler>()
			.Select(parameter => parameter.ParameterType);

		Assert.IsFalse(
			dependencies.Any(type => type == typeof(IObjectStore) || type == typeof(IHrefTokenCodec)),
			$"{nameof(LockRouteHandler)} took an object-store or transfer-token dependency.");
	}

	/// <summary>
	/// The three the issue named, and the reason the split was worth making.
	/// </summary>
	[TestMethod]
	public void LockHandler_HoldsTheLockDependencies()
	{
		IEnumerable<Type> dependencies = ConstructorParametersOf<LockRouteHandler>()
			.Select(parameter => parameter.ParameterType);

		CollectionAssert.IsSubsetOf(
			new[] { typeof(LockListService), typeof(ILockSnapshotStore), typeof(LockFanOut) },
			dependencies.ToList());
	}

	/// <summary>
	/// The dispatcher owning a lock dependency directly is how the previous shape started.
	/// </summary>
	[TestMethod]
	public void Dispatcher_RoutesLocksThroughTheLockHandlerRatherThanHoldingItsDependencies()
	{
		List<Type> dependencies = [.. ConstructorParametersOf<GitLfsCacheHandler>()
			.Select(parameter => parameter.ParameterType)];

		CollectionAssert.Contains(dependencies, typeof(LockRouteHandler));

		foreach (Type lockDependency in new[]
		{
			typeof(LockListService),
			typeof(ILockSnapshotStore),
			typeof(LockFanOut),
		})
		{
			CollectionAssert.DoesNotContain(
				dependencies,
				lockDependency,
				$"{nameof(GitLfsCacheHandler)} holds {lockDependency.Name} directly; it belongs to "
				+ $"{nameof(LockRouteHandler)}.");
		}
	}
}
