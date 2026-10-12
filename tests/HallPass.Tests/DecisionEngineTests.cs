using System.Collections.Generic;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class DecisionEngineTests
{
	private static EvidenceItem E(string source, double confidence) => new EvidenceItem(source, Verdict.Explicit, confidence, "");

	private static EvidenceItem C(string source, double confidence) => new EvidenceItem(source, Verdict.Clean, confidence, "");

	private static DecisionResult Decide(params EvidenceItem[] evidence) => DecisionEngine.Decide(ManualOverride.None, evidence, DecisionSettings.Balanced);

	[Fact]
	public void AManualOverrideAlwaysWins()
	{
		Assert.Equal(Decision.Clean, DecisionEngine.Decide(ManualOverride.Clean, new[] { E("lyrics", 1.0) }, DecisionSettings.Balanced).Decision);
		Assert.Equal(Decision.Explicit, DecisionEngine.Decide(ManualOverride.Explicit, new[] { C("embedded", 1.0) }, DecisionSettings.Balanced).Decision);
		Assert.Equal(Decision.Explicit, DecisionEngine.Decide(ManualOverride.Explicit, new List<EvidenceItem>(), DecisionSettings.Balanced, skipped: true).Decision);
	}

	[Fact]
	public void SkippedTracksAreSkipped()
	{
		Assert.Equal(Decision.Skipped, DecisionEngine.Decide(ManualOverride.None, new[] { E("lyrics", 0.9) }, DecisionSettings.Balanced, skipped: true).Decision);
	}

	[Fact]
	public void NoEvidenceMeansReview()
	{
		DecisionResult none = Decide();
		Assert.Equal(Decision.Review, none.Decision);
		Assert.Equal(ReviewReason.NoEvidence, none.ReviewReason);
		// evidence that says nothing, or with no confidence, is no evidence
		Assert.Equal(ReviewReason.NoEvidence, Decide(new EvidenceItem("lyrics", Verdict.None, 0, "too little text")).ReviewReason);
		Assert.Equal(ReviewReason.NoEvidence, Decide(E("x", 0), C("y", 0)).ReviewReason);
	}

	[Fact]
	public void ConfidentExplicitEvidenceIsExplicit()
	{
		DecisionResult r = Decide(E("lyrics", 0.75));
		Assert.Equal(Decision.Explicit, r.Decision);
		Assert.Equal(0.75, r.Confidence);
	}

	[Fact]
	public void WeakExplicitEvidenceIsASuspect()
	{
		DecisionResult r = Decide(E("lyrics", 0.5));
		Assert.Equal(Decision.Review, r.Decision);
		Assert.Equal(ReviewReason.Suspected, r.ReviewReason);
	}

	[Fact]
	public void OnlyStrongCleanEvidenceIsClean()
	{
		Assert.Equal(Decision.Clean, Decide(C("embedded", 0.9)).Decision);
		DecisionResult weak = Decide(C("lyrics", 0.5));
		Assert.Equal(Decision.Review, weak.Decision);
		Assert.Equal(ReviewReason.WeakClean, weak.ReviewReason);
	}

	[Fact]
	public void DisagreementGoesToReviewUnlessExplicitIsCertain()
	{
		DecisionResult conflict = Decide(E("lyrics", 0.8), C("embedded", 0.9));
		Assert.Equal(Decision.Review, conflict.Decision);
		Assert.Equal(ReviewReason.Conflict, conflict.ReviewReason);
		Assert.Equal(Decision.Explicit, Decide(E("online", 0.95), C("embedded", 0.9)).Decision);
		// explicit below the automatic bar plus any clean evidence is still a conflict, not a plain suspect
		Assert.Equal(ReviewReason.Conflict, Decide(E("lyrics", 0.5), C("embedded", 0.9)).ReviewReason);
	}

	[Fact]
	public void TheConservativePresetNeedsTwoSourcesForCleanAndTrustsLessExplicitEvidence()
	{
		DecisionSettings s = DecisionSettings.Conservative;
		Assert.Equal(ReviewReason.WeakClean, DecisionEngine.Decide(ManualOverride.None, new[] { C("embedded", 0.9) }, s).ReviewReason);
		Assert.Equal(Decision.Clean, DecisionEngine.Decide(ManualOverride.None, new[] { C("embedded", 0.9), C("online", 0.95) }, s).Decision);
		Assert.Equal(Decision.Explicit, DecisionEngine.Decide(ManualOverride.None, new[] { E("title", 0.6) }, s).Decision);
		// two clean items from one source are still one source
		Assert.Equal(ReviewReason.WeakClean, DecisionEngine.Decide(ManualOverride.None, new[] { C("online", 0.9), C("online", 0.95) }, s).ReviewReason);
	}

	[Fact]
	public void TheRelaxedPresetAcceptsLessCleanEvidenceAndWantsMoreExplicitEvidence()
	{
		DecisionSettings s = DecisionSettings.Relaxed;
		Assert.Equal(Decision.Clean, DecisionEngine.Decide(ManualOverride.None, new[] { C("online", 0.8) }, s).Decision);
		Assert.Equal(Decision.Review, DecisionEngine.Decide(ManualOverride.None, new[] { E("lyrics", 0.8) }, s).Decision);
		Assert.Equal(Decision.Explicit, DecisionEngine.Decide(ManualOverride.None, new[] { E("online", 0.9) }, s).Decision);
	}

	[Fact]
	public void TheStrongestEvidenceCounts()
	{
		Assert.Equal(Decision.Explicit, Decide(E("title", 0.6), E("lyrics", 0.75), E("lyrics2", 0.5)).Decision);
	}
}
