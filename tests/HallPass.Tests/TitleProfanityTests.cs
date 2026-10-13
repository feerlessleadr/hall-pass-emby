using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class TitleProfanityTests
{
	private static readonly ProfanityScanner Scanner = new ProfanityScanner(WordList.Default());

	[Theory]
	[InlineData("Fuck Everybody Else")]
	[InlineData("Fuck Wiping!")]
	[InlineData("Fuck You Tom")]
	[InlineData("What the f*ck")]
	[InlineData("Bullshit")]
	public void ASevereWordInTheTitleIsStrongExplicitEvidence(string title)
	{
		EvidenceItem e = Assert.Single(TitleHeuristics.TitleProfanity(title, Scanner));
		Assert.Equal("title", e.Source);
		Assert.Equal(Verdict.Explicit, e.Verdict);
		Assert.Equal(TitleHeuristics.SevereWordInTitle, e.Confidence);
	}

	[Fact]
	public void AModerateWordIsOnlyAHint()
	{
		EvidenceItem e = Assert.Single(TitleHeuristics.TitleProfanity("Dammit", Scanner));
		Assert.Equal(TitleHeuristics.ModerateWordInTitle, e.Confidence);
		Assert.True(e.Confidence < 0.75); // alone it never decides "explicit"
	}

	[Theory]
	[InlineData("For All the Ladies")]
	[InlineData("Going Away to College")]
	[InlineData("Hell")] // mild: scores nothing
	[InlineData("Scunthorpe United")]
	[InlineData("Classic Assistant Glass")]
	[InlineData("")]
	public void CleanTitlesGiveNothing(string title)
	{
		Assert.Empty(TitleHeuristics.TitleProfanity(title, Scanner));
	}

	[Fact]
	public void YourOwnWordEditsApplyToTitlesToo()
	{
		ProfanityScanner own = new ProfanityScanner(WordList.Default().WithEdits(new[] { "zorp" }, null, null, null, new[] { "fuck", "fucking" }));
		Assert.Single(TitleHeuristics.TitleProfanity("A Zorp Song", own));
		Assert.Empty(TitleHeuristics.TitleProfanity("Fuck Everybody Else", own));
	}

	[Fact]
	public void ATitleWordAloneMakesATrackExplicitNotReview()
	{
		System.Collections.Generic.IReadOnlyList<EvidenceItem> evidence = TitleHeuristics.TitleProfanity("Fuck You Tom", Scanner);
		DecisionResult d = DecisionEngine.Decide(ManualOverride.None, evidence, DecisionSettings.Balanced);
		Assert.Equal(Decision.Explicit, d.Decision);
	}
}
