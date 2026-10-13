using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class AmbiguousWordTests
{
	private static readonly ProfanityScanner Scanner = new ProfanityScanner(WordList.Default());

	private static string Filler => " the quick brown fox jumps over the lazy dog while we all sing along to the song one more time again";

	[Theory]
	[InlineData("He cocked his head and smiled")]
	[InlineData("She is cocking the gun now")]
	[InlineData("a cocky kind of boy")]
	public void InflectionsOfCockAreNotCock(string line)
	{
		Assert.Empty(Scanner.Scan(line + Filler).Hits);
	}

	[Fact]
	public void CockAsWrittenStillCountsButOnlyAsASuspectWhenHeardOnce()
	{
		ScanResult once = Scanner.Scan("what a cock" + Filler);
		WordHit hit = Assert.Single(once.Hits);
		Assert.Equal("cock", hit.Term);
		Assert.Equal(1, hit.Weight);
		Assert.Equal(EvidenceVerdictFor(once), Verdict.Explicit);
		Assert.True(Scanner.ToEvidence(once).Confidence < 0.75); // a suspect: goes to review, not straight to explicit
		ScanResult twice = Scanner.Scan("what a cock\nyou cock" + Filler);
		Assert.Equal(6, twice.Score);
		Assert.Equal(0.75, Scanner.ToEvidence(twice).Confidence);
	}

	private static Verdict EvidenceVerdictFor(ScanResult r) => Scanner.ToEvidence(r).Verdict;

	[Fact]
	public void ADickWrittenWithACapitalInTheMiddleOfALineIsAName()
	{
		ScanResult r = Scanner.Scan("Wait for Dick and Jane to arrive" + Filler);
		WordHit hit = Assert.Single(r.Hits);
		Assert.Equal("dick (a name)", hit.Term);
		Assert.Equal(0, r.Score);
		EvidenceItem e = Scanner.ToEvidence(r);
		Assert.Equal(Verdict.Clean, e.Verdict);
		Assert.Contains("a name", e.Detail);
	}

	[Theory]
	[InlineData("Dick is here" )] // starts the line: could be either
	[InlineData("what a dick")]
	[InlineData("WHAT A DICK YOU ARE")] // a line in capitals says nothing about names
	public void ADickThatDoesNotLookLikeANameIsStillASuspect(string line)
	{
		ScanResult r = Scanner.Scan(line + "\n" + Filler);
		WordHit hit = Assert.Single(r.Hits);
		Assert.Equal("dick", hit.Term);
		Assert.Equal(1, hit.Weight);
	}

	[Fact]
	public void CapitalsOnALaterLineAreStillJudgedByTheirOwnLine()
	{
		ScanResult r = Scanner.Scan("first line of words\nsing about Moby Dick tonight\nanother line" + Filler);
		Assert.Equal("dick (a name)", Assert.Single(r.Hits).Term);
		ScanResult second = Scanner.Scan("first line of words\nDick on its own line\nanother line" + Filler);
		Assert.Equal("dick", Assert.Single(second.Hits).Term);
	}

	[Fact]
	public void OtherSevereWordsAreUntouchedByTheNameRule()
	{
		ScanResult r = Scanner.Scan("Hey Shit and Fuck" + Filler);
		Assert.Contains(r.Hits, h => h.Term == "shit" && h.Weight == 3);
		Assert.Contains(r.Hits, h => h.Term == "fuck" && h.Weight == 3);
	}

	[Fact]
	public void YourOwnEditsKeepTheseRules()
	{
		ProfanityScanner own = new ProfanityScanner(WordList.Default().WithEdits(new[] { "zorp" }, null, null, null, null));
		Assert.Equal("dick (a name)", Assert.Single(own.Scan("hello Dick and Jane" + Filler).Hits).Term);
		Assert.Empty(own.Scan("he cocked his hat" + Filler).Hits);
		// ignoring the word removes it altogether
		ProfanityScanner ignored = new ProfanityScanner(WordList.Default().WithEdits(null, null, null, null, new[] { "dick" }));
		Assert.Empty(ignored.Scan("what a dick" + Filler).Hits);
	}

	[Fact]
	public void TheListReadsTheNewKindsOfWords()
	{
		WordList list = WordList.Default();
		Assert.Contains("cock", list.ExactOnly);
		Assert.Contains("dick", list.NameLike);
		Assert.Contains("cocks", list.Ambiguous);
		WordList parsed = WordList.Parse("{\"severe\":[\"zap\"],\"exactOnly\":[\" Zap \"],\"nameLike\":[\"zap\"],\"ambiguous\":[\"zap\"]}");
		Assert.Contains("zap", parsed.ExactOnly);
		Assert.Contains("zap", parsed.NameLike);
		Assert.Contains("zap", parsed.Ambiguous);
		Assert.Empty(WordList.Parse("{\"severe\":[\"zap\"]}").ExactOnly);
	}
}

public class LyricsEvidenceTests
{
	private static readonly ProfanityScanner Scanner = new ProfanityScanner(WordList.Default());

	private static ScanResult Clean() => new ScanResult(0, 300, new List<WordHit>());

	private static ScanResult Explicit() => new ScanResult(6, 300, new List<WordHit> { new WordHit("titty", 3, 2) });

	private static ScanResult Short() => new ScanResult(0, 4, new List<WordHit>());

	[Fact]
	public void AnExplicitSourceAndACleanSourceStaySeparateSoTheTrackIsAConflict()
	{
		IReadOnlyList<EvidenceItem> ev = LyricsEvidence.Combine(new[] { ("the lyrics embedded in the audio file", Explicit()), ("the file Song.lrc", Clean()) }, Scanner);
		Assert.Equal(2, ev.Count);
		Assert.Contains(ev, e => e.Verdict == Verdict.Explicit && e.Detail.Contains("embedded"));
		Assert.Contains(ev, e => e.Verdict == Verdict.Clean && e.Detail.Contains("Song.lrc"));
		DecisionResult d = DecisionEngine.Decide(ManualOverride.None, ev, DecisionSettings.Balanced);
		Assert.Equal(Decision.Review, d.Decision);
		Assert.Equal(ReviewReason.Conflict, d.ReviewReason);
	}

	[Fact]
	public void SourcesThatAgreeAreOneLineNamingBoth()
	{
		IReadOnlyList<EvidenceItem> ev = LyricsEvidence.Combine(new[] { ("the lyrics embedded in the audio file", Clean()), ("the file Song.lrc", Clean()) }, Scanner);
		EvidenceItem e = Assert.Single(ev);
		Assert.Equal(Verdict.Clean, e.Verdict);
		Assert.Contains("embedded", e.Detail);
		Assert.Contains("Song.lrc", e.Detail);
	}

	[Fact]
	public void TwoExplicitSourcesGiveOneExplicitLine()
	{
		EvidenceItem e = Assert.Single(LyricsEvidence.Combine(new[] { ("A", Explicit()), ("B", Explicit()) }, Scanner));
		Assert.Equal(Verdict.Explicit, e.Verdict);
		Assert.Equal(Decision.Explicit, DecisionEngine.Decide(ManualOverride.None, new[] { e }, DecisionSettings.Balanced).Decision);
	}

	[Fact]
	public void ASourceWithTooLittleTextIsIgnoredWhenAnotherHasSomething()
	{
		EvidenceItem e = Assert.Single(LyricsEvidence.Combine(new[] { ("A", Short()), ("B", Clean()) }, Scanner));
		Assert.Equal(Verdict.Clean, e.Verdict);
	}

	[Fact]
	public void IfEverySourceIsTooShortOneNoteRemainsAndNoSourcesGiveNothing()
	{
		EvidenceItem e = Assert.Single(LyricsEvidence.Combine(new[] { ("A", Short()), ("B", Short()) }, Scanner));
		Assert.Equal(Verdict.None, e.Verdict);
		Assert.Empty(LyricsEvidence.Combine(new (string, ScanResult)[0], Scanner));
	}
}
