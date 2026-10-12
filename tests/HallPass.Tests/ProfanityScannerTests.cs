using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class ProfanityScannerTests
{
	private static ProfanityScanner Scanner(ScannerOptions? options = null) => new ProfanityScanner(WordList.Default(), options);

	private static int Score(string text) => Scanner().Scan(text).Score;

	private const string CleanVerse = "Walking down the road with the sun on my face and a song in my heart, nothing but blue sky and open fields ahead of me";

	[Fact]
	public void OrdinaryLyricsScoreNothing()
	{
		ScanResult r = Scanner().Scan(CleanVerse);
		Assert.Equal(0, r.Score);
		Assert.Empty(r.Hits);
		Assert.True(r.WordCount > 15);
	}

	[Theory]
	[InlineData("Scunthorpe")]
	[InlineData("I assume the class will pass the assessment")]
	[InlineData("a plate of shiitake and a bass guitar")]
	[InlineData("Hello, Dickens and Dickinson at the peacock cafe")]
	[InlineData("shitake mushrooms")]
	public void InnocentWordsContainingBadLettersAreNotHits(string text)
	{
		Assert.Equal(0, Score(text));
	}

	[Theory]
	[InlineData("damned", "damn")]
	[InlineData("damns", "damn")]
	[InlineData("pissing", "piss")]
	[InlineData("crappy", "crap")]
	[InlineData("bitching", "bitch")]
	[InlineData("assed", "ass")]
	[InlineData("hells", "hell")]
	public void CommonInflectionsCount(string word, string term)
	{
		ScanResult r = Scanner().Scan("this is a " + word + " thing");
		Assert.Contains(r.Hits, (WordHit h) => h.Term == term);
	}

	[Theory]
	[InlineData("fuck", 3)]
	[InlineData("FUCKING", 3)]
	[InlineData("motherfuckin'", 3)]
	[InlineData("motherfuckin’", 3)]
	[InlineData("shit", 3)]
	[InlineData("bullshitter", 3)]
	[InlineData("bitch", 3)]
	[InlineData("pussy", 3)]
	[InlineData("piss", 2)]
	[InlineData("pissing", 2)]
	[InlineData("ass", 2)]
	[InlineData("asshole", 2)]
	[InlineData("pissed", 0)]
	[InlineData("damn", 0)]
	[InlineData("hell", 0)]
	[InlineData("crap", 0)]
	[InlineData("arse", 0)]
	public void TiersCarryTheirWeights(string word, int weight)
	{
		Assert.Equal(weight, Score("oh " + word + " oh"));
	}

	[Theory]
	[InlineData("f*ck")]
	[InlineData("f**k")]
	[InlineData("sh!t")]
	[InlineData("$hit")]
	[InlineData("sh1t")]
	[InlineData("fuuuuck")]
	[InlineData("shiiiit")]
	[InlineData("f*cking")]
	[InlineData("a$$")]
	[InlineData("asssss")]
	[InlineData("ｆｕｃｋ")]
	public void MaskedAndStretchedSpellingsAreCaught(string word)
	{
		Assert.True(Score("well " + word + " that") > 0, word);
	}

	[Theory]
	[InlineData("***")]
	[InlineData("a*b")]
	[InlineData("!!!")]
	[InlineData("1999 and 2024 at 5 o'clock")]
	[InlineData("wow!!! really?!")]
	public void SymbolsAndNumbersAloneAreNotHits(string text)
	{
		Assert.Equal(0, Score(text));
	}

	[Fact]
	public void TimingMarksAndMetadataLinesAreStripped()
	{
		string lrc = "[ar:Some Artist]\n[ti:Some Song]\n[offset:0]\n[00:12.34]Hello there my friend\n[00:20.00]oh <00:21.10>piss <00:21.80>it\n";
		ScanResult r = Scanner().Scan(lrc);
		Assert.Equal(2, r.Score);
		Assert.Equal("piss", Assert.Single(r.Hits).Term);
		Assert.Equal(7, r.WordCount); // hello there my friend oh piss it
		Assert.DoesNotContain("00", ProfanityScanner.StripTimings(lrc));
		Assert.DoesNotContain("Some Artist", ProfanityScanner.StripTimings(lrc));
	}

	[Fact]
	public void RepeatedWordsCountEachTimeUnlessCapped()
	{
		Assert.Equal(6, Score("piss piss piss"));
		ScannerOptions cap = new ScannerOptions { PerTermCap = 1 };
		Assert.Equal(2, Scanner(cap).Scan("piss piss piss").Score);
	}

	[Fact]
	public void HitsAreSortedByTheirShareOfTheScoreAndCounted()
	{
		ScanResult r = Scanner().Scan("damn fuck shit shit");
		Assert.Equal(0 + 3 + 6, r.Score);
		Assert.Equal(new[] { "shit", "fuck", "damn" }, r.Hits.Select((WordHit h) => h.Term).ToArray());
		Assert.Equal(2, r.Hits.Single((WordHit h) => h.Term == "shit").Count);
	}

	[Fact]
	public void TheAllowlistWinsOverEverything()
	{
		WordList list = WordList.Parse("{\"moderate\":[\"hell\"],\"stems\":{\"hel\":2},\"allow\":[\"hello\",\"hell's\"]}");
		ProfanityScanner scanner = new ProfanityScanner(list);
		Assert.Equal(0, scanner.Scan("hello").Score);
		Assert.True(scanner.Scan("hell").Score > 0);
		Assert.True(scanner.Scan("shell").Score > 0); // a stem counts inside words unless allowed: that is what the allowlist is for
		Assert.Equal(0, scanner.Scan("hell's").Score);
	}

	[Fact]
	public void EvidenceMapsTheScore()
	{
		ProfanityScanner scanner = Scanner();
		EvidenceItem explicitEvidence = scanner.ToEvidence(scanner.Scan("what the fuck"));
		Assert.Equal(Verdict.Explicit, explicitEvidence.Verdict);
		Assert.Equal(0.75, explicitEvidence.Confidence);
		Assert.Equal("lyrics", explicitEvidence.Source);

		EvidenceItem suspect = scanner.ToEvidence(scanner.Scan("oh piss"));
		Assert.Equal(Verdict.Explicit, suspect.Verdict);
		Assert.Equal(0.5, suspect.Confidence);

		EvidenceItem clean = scanner.ToEvidence(scanner.Scan(CleanVerse));
		Assert.Equal(Verdict.Clean, clean.Verdict);
		Assert.Equal(0.5, clean.Confidence);

		// mild words are recorded in the detail but never score: still clean-looking
		EvidenceItem mildOnly = scanner.ToEvidence(scanner.Scan(CleanVerse + " damn damn"));
		Assert.Equal(Verdict.Clean, mildOnly.Verdict);
		Assert.Contains("damn x2", mildOnly.Detail);

		// too little text to call anything clean (an instrumental's placeholder)
		Assert.Equal(Verdict.None, scanner.ToEvidence(scanner.Scan("[Instrumental]")).Verdict);
		Assert.Equal(Verdict.None, scanner.ToEvidence(scanner.Scan("")).Verdict);
	}

	[Fact]
	public void AVeryHighScoreIsNearCertain()
	{
		ProfanityScanner scanner = Scanner();
		EvidenceItem lots = scanner.ToEvidence(scanner.Scan(string.Join(" ", Enumerable.Repeat("shit", 4)) + " fuck fuck and some more words to fill the lyrics up properly so there is enough text"));
		Assert.Equal(Verdict.Explicit, lots.Verdict);
		Assert.Equal(0.95, lots.Confidence);
		Assert.Contains("very high", lots.Detail);
		// just below the line stays at the normal explicit confidence
		EvidenceItem some = scanner.ToEvidence(scanner.Scan("shit shit shit and then some more words so the lyrics are long enough to count as text here"));
		Assert.Equal(0.75, some.Confidence);
		// and it beats an online "not explicit" in the decision
		DecisionResult r = DecisionEngine.Decide(ManualOverride.None, new[] { lots, new EvidenceItem("deezer", Verdict.Clean, 0.85, "") }, DecisionSettings.Balanced);
		Assert.Equal(Decision.Explicit, r.Decision);
	}

	[Fact]
	public void EvidenceNeverCarriesTheLyrics()
	{
		ProfanityScanner scanner = Scanner();
		EvidenceItem e = scanner.ToEvidence(scanner.Scan("sunshine and rainbows all day long, then a damn good night's sleep for everybody who walks down this road with a song"));
		Assert.DoesNotContain("sunshine", e.Detail);
		Assert.DoesNotContain("rainbows", e.Detail);
		Assert.Contains("damn", e.Detail);
	}

	[Fact]
	public void CustomThresholdsAreUsed()
	{
		ScannerOptions strict = new ScannerOptions { ExplicitScore = 1, ExplicitConfidence = 0.9 };
		ProfanityScanner scanner = Scanner(strict);
		EvidenceItem e = scanner.ToEvidence(scanner.Scan("oh piss"));
		Assert.Equal(Verdict.Explicit, e.Verdict);
		Assert.Equal(0.9, e.Confidence);
	}
}
