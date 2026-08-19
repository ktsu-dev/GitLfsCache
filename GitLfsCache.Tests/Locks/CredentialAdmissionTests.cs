// Copyright (c) 2023-2026 ktsu-dev contributors

namespace ktsu.GitLfsCache.Tests.Locks;

using ktsu.GitLfsCache.Configuration;
using ktsu.GitLfsCache.Locks;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Time.Testing;

[TestClass]
public class CredentialAdmissionTests
{
	private const string Repository = "owner/repo.git/info/lfs";
	private const string Credential = "Basic dXNlcjp0b2tlbg==";

	private static (CredentialAdmission Admission, FakeTimeProvider Time) Build(
		TimeSpan? admissionTtl = null)
	{
		GitLfsCacheOptions options = new()
		{
			Locks = new LocksOptions { AdmissionTtl = admissionTtl ?? TimeSpan.FromMinutes(1) },
		};

		FakeTimeProvider time = new(new DateTimeOffset(2026, 8, 19, 9, 47, 0, TimeSpan.Zero));

		return (new CredentialAdmission(Options.Create(options), time), time);
	}

	[TestMethod]
	public void IsAdmitted_BeforeAnyUpstreamSuccess_IsFalse()
	{
		// The whole point: nothing is admitted until upstream actually said yes.
		(CredentialAdmission admission, _) = Build();

		Assert.IsFalse(admission.IsAdmitted("github", Repository, Credential));
	}

	[TestMethod]
	public void IsAdmitted_AfterAdmit_IsTrue()
	{
		(CredentialAdmission admission, _) = Build();

		admission.Admit("github", Repository, Credential);

		Assert.IsTrue(admission.IsAdmitted("github", Repository, Credential));
	}

	[TestMethod]
	public void IsAdmitted_AfterTheTtl_IsFalseAgain()
	{
		(CredentialAdmission admission, FakeTimeProvider time) = Build(TimeSpan.FromMinutes(1));

		admission.Admit("github", Repository, Credential);
		time.Advance(TimeSpan.FromMinutes(1));

		// This is the window in which a credential revoked upstream still reads listings. It has to
		// actually close.
		Assert.IsFalse(admission.IsAdmitted("github", Repository, Credential));
	}

	[TestMethod]
	public void IsAdmitted_JustBeforeTheTtl_IsStillTrue()
	{
		(CredentialAdmission admission, FakeTimeProvider time) = Build(TimeSpan.FromMinutes(1));

		admission.Admit("github", Repository, Credential);
		time.Advance(TimeSpan.FromSeconds(59));

		Assert.IsTrue(admission.IsAdmitted("github", Repository, Credential));
	}

	[TestMethod]
	public void IsAdmitted_ADifferentCredential_IsNotAdmitted()
	{
		(CredentialAdmission admission, _) = Build();

		admission.Admit("github", Repository, Credential);

		Assert.IsFalse(admission.IsAdmitted("github", Repository, "Basic c29tZW9uZTplbHNl"));
	}

	[TestMethod]
	public void IsAdmitted_ADifferentRepository_IsNotAdmitted()
	{
		// Admission is per repository. Read access to one proves nothing about another.
		(CredentialAdmission admission, _) = Build();

		admission.Admit("github", Repository, Credential);

		Assert.IsFalse(admission.IsAdmitted("github", "owner/other.git/info/lfs", Credential));
	}

	[TestMethod]
	public void IsAdmitted_ADifferentUpstream_IsNotAdmitted()
	{
		(CredentialAdmission admission, _) = Build();

		admission.Admit("github", Repository, Credential);

		Assert.IsFalse(admission.IsAdmitted("ado", Repository, Credential));
	}

	[TestMethod]
	public void Key_CannotBeConfusedAcrossFields()
	{
		// Without a separator that cannot appear in an upstream key, "github" + "a/b" and "github/a" +
		// "b" would hash identically and one repository's admission would serve another's.
		(CredentialAdmission admission, _) = Build();

		admission.Admit("github", "a/b", Credential);

		Assert.IsFalse(admission.IsAdmitted("github/a", "b", Credential));
	}

	[TestMethod]
	[DataRow(null)]
	[DataRow("")]
	public void IsAdmitted_NoCredential_IsNeverAdmitted(string? authorization)
	{
		// Admitting an anonymous caller would mean serving a listing to someone who proved nothing.
		(CredentialAdmission admission, _) = Build();

		admission.Admit("github", Repository, authorization);

		Assert.IsFalse(admission.IsAdmitted("github", Repository, authorization));
	}

	[TestMethod]
	public void Admit_Twice_ExtendsTheWindowFromTheSecondTime()
	{
		(CredentialAdmission admission, FakeTimeProvider time) = Build(TimeSpan.FromMinutes(1));

		admission.Admit("github", Repository, Credential);
		time.Advance(TimeSpan.FromSeconds(50));
		admission.Admit("github", Repository, Credential);
		time.Advance(TimeSpan.FromSeconds(50));

		Assert.IsTrue(admission.IsAdmitted("github", Repository, Credential));
	}
}
