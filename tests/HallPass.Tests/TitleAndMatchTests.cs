using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class TitleHeuristicsTests
{
	private static EvidenceItem[] Look(string? title, string? album = null, params string[] folders) => TitleHeuristics.Evaluate(title, album, folders).ToArray();

	[Theory]
	[InlineData("Song (Explicit)")]
	[InlineData("Song [Explicit]")]
	[InlineData("Song (Explicit Version)")]
	[InlineData("Song (E)")]
	[InlineData("Song [e]")]
	[InlineData("Song (Live) (NSFW)")]
	public void ExplicitMarkersInBrackets(string title)
	{
		EvidenceItem e = Assert.Single(Look(title));
		Assert.Equal(Verdict.Explicit, e.Verdict);
		Assert.Equal(TitleHeuristics.ExplicitMarker, e.Confidence);
		Assert.Equal("title", e.Source);
	}

	[Theory]
	[InlineData("Song (Clean)")]
	[InlineData("Song [Clean Version]")]
	[InlineData("Song (Censored)")]
	[InlineData("Song (Edited)")]
	[InlineData("Song (Not Explicit)")]
	[InlineData("Song - SFW")]
	public void CleanMarkers(string title)
	{
		EvidenceItem e = Assert.Single(Look(title));
		Assert.Equal(Verdict.Clean, e.Verdict);
	}

	[Fact]
	public void ARadioEditIsOnlyAHint()
	{
		EvidenceItem e = Assert.Single(Look("Song (Radio Edit)"));
		Assert.Equal(Verdict.Clean, e.Verdict);
		Assert.Equal(TitleHeuristics.RadioEditMarker, e.Confidence);
	}

	[Theory]
	[InlineData("Explicit Lyrics")]
	[InlineData("The Clean Slate")]
	[InlineData("Song")]
	[InlineData("Unexplicit Content Warning Blues")]
	public void WordsOutsideBracketsAreNotMarkers(string title)
	{
		Assert.Empty(Look(title));
	}

	[Fact]
	public void NsfwIsExplicitAndSfwIsCleanAndNsfwIsNotSfw()
	{
		EvidenceItem nsfw = Assert.Single(Look(null, null, "Tickets to My Downfall (nsfw)"));
		Assert.Equal(Verdict.Explicit, nsfw.Verdict);
		Assert.Equal("folder", nsfw.Source);
		EvidenceItem sfw = Assert.Single(Look(null, null, "sun to me (sfw)"));
		Assert.Equal(Verdict.Clean, sfw.Verdict);
	}

	[Fact]
	public void TitleAlbumAndFoldersAreAllLookedAt()
	{
		EvidenceItem[] found = Look("Song (Clean)", "Album [Explicit]", "Artist", "Album (NSFW)");
		Assert.Equal(new[] { "title", "album", "folder" }, found.Select((EvidenceItem e) => e.Source).ToArray());
		Assert.Equal(new[] { Verdict.Clean, Verdict.Explicit, Verdict.Explicit }, found.Select((EvidenceItem e) => e.Verdict).ToArray());
	}

	[Fact]
	public void NothingToLookAtGivesNothing()
	{
		Assert.Empty(TitleHeuristics.Evaluate(null, null, null));
		Assert.Empty(TitleHeuristics.Evaluate("", "  ", new string[0]));
	}
}

public class TrackMatcherTests
{
	[Theory]
	[InlineData("Song Title (feat. Someone Else)", "song title")]
	[InlineData("Song Title [ft. Someone]", "song title")]
	[InlineData("Song Title - 2011 Remaster", "song title")]
	[InlineData("Song Title (Remastered 2009)", "song title")]
	[InlineData("Song Title - Single Version", "song title")]
	[InlineData("Café del Mar!", "cafe del mar")]
	[InlineData("Rock & Roll", "rock and roll")]
	[InlineData("Don't Stop", "don t stop")]
	public void TitlesAreNormalised(string title, string expected)
	{
		Assert.Equal(expected, TrackMatcher.NormalizeTitle(title));
	}

	[Theory]
	[InlineData("Song (Radio Edit)")]
	[InlineData("Song (Clean)")]
	[InlineData("Song (Explicit)")]
	[InlineData("Song (Live)")]
	public void VersionWordsThatChangeTheRecordingAreKept(string title)
	{
		Assert.NotEqual(TrackMatcher.NormalizeTitle("Song"), TrackMatcher.NormalizeTitle(title));
	}

	[Theory]
	[InlineData("The Cranberries", "cranberries")]
	[InlineData("Imagine Dragons ft. Elisa", "imagine dragons")]
	[InlineData("Artist One; Artist Two", "artist one")]
	[InlineData("Simon & Garfunkel", "simon and garfunkel")]
	[InlineData("blink‐182", "blink 182")]
	public void ArtistsAreNormalisedToTheMainArtist(string artist, string expected)
	{
		Assert.Equal(expected, TrackMatcher.NormalizeArtist(artist));
	}

	private static TrackInfo Local(double? seconds = 215, string? album = "The Album", string? isrc = null) => new TrackInfo("Song Title", "The Artist", album, seconds, isrc);

	[Fact]
	public void AnIsrcMatchBeatsEverythingElse()
	{
		MatchResult r = TrackMatcher.Match(Local(isrc: "US-UG1-25-03230"), new TrackInfo("Totally Different", "Someone Else", null, 10, "USUG12503230"));
		Assert.Equal(TrackMatcher.IsrcMatch, r.Confidence);
	}

	[Fact]
	public void MalformedIsrcsAreIgnored()
	{
		MatchResult r = TrackMatcher.Match(Local(isrc: "123"), new TrackInfo("Other", "Other", null, 10, "123"));
		Assert.Equal(0, r.Confidence);
	}

	[Fact]
	public void SameTitleArtistAndLengthIsAStrongMatch()
	{
		MatchResult r = TrackMatcher.Match(Local(album: null), new TrackInfo("Song Title (feat. X)", "The Artist", null, 217, null));
		Assert.Equal(TrackMatcher.TitleArtistDurationMatch, r.Confidence);
		MatchResult withAlbum = TrackMatcher.Match(Local(), new TrackInfo("Song Title", "Artist", "The  Album!", 214, null));
		Assert.Equal(TrackMatcher.TitleArtistDurationMatch + TrackMatcher.AlbumBonus, withAlbum.Confidence, 6);
	}

	[Fact]
	public void AnotherLengthMeansAnotherVersion()
	{
		MatchResult r = TrackMatcher.Match(Local(), new TrackInfo("Song Title", "The Artist", "The Album", 190, null));
		Assert.Equal(TrackMatcher.OtherVersion, r.Confidence);
		Assert.True(r.Confidence < 0.75, "must stay below the automatic explicit threshold");
	}

	[Fact]
	public void FuzzyMatchesStayAtOrBelowTheCap()
	{
		MatchResult r = TrackMatcher.Match(Local(seconds: null), new TrackInfo("Song Titl", "The Artist", null, null, null));
		Assert.InRange(r.Confidence, 0.01, TrackMatcher.FuzzyCap);
		MatchResult sameButUnknownLength = TrackMatcher.Match(Local(seconds: null), new TrackInfo("Song Title", "The Artist", null, 200, null));
		Assert.InRange(sameButUnknownLength.Confidence, 0.01, TrackMatcher.FuzzyCap);
	}

	[Fact]
	public void DifferentSongsDoNotMatch()
	{
		Assert.Equal(0, TrackMatcher.Match(Local(), new TrackInfo("Another Song", "The Artist", "The Album", 215, null)).Confidence);
		Assert.Equal(0, TrackMatcher.Match(Local(), new TrackInfo("Song Title", "A Different Band", "The Album", 215, null)).Confidence);
		Assert.Equal(0, TrackMatcher.Match(new TrackInfo("", "", null, null, null), new TrackInfo("", "", null, null, null)).Confidence);
	}

	[Fact]
	public void TheToleranceCanBeChanged()
	{
		TrackInfo other = new TrackInfo("Song Title", "The Artist", null, 220, null);
		Assert.Equal(TrackMatcher.OtherVersion, TrackMatcher.Match(Local(album: null), other).Confidence);
		Assert.Equal(TrackMatcher.TitleArtistDurationMatch, TrackMatcher.Match(Local(album: null), other, 6).Confidence);
	}

	[Theory]
	[InlineData("", "", 1.0)]
	[InlineData("abc", "abc", 1.0)]
	[InlineData("abc", "", 0.0)]
	[InlineData("kitten", "sitting", 1.0 - 3.0 / 7.0)]
	public void SimilarityFromEditDistance(string a, string b, double expected)
	{
		Assert.Equal(expected, TrackMatcher.Similarity(a, b), 6);
	}
}

public class WordListTests
{
	[Fact]
	public void TiersGetTheirWeightsAndTheHigherWeightWins()
	{
		WordList list = WordList.Parse("{\"severe\":[\"aaa\"],\"moderate\":[\"bbb\",\"aaa\"],\"mild\":[\"ccc\",\"BBB\"],\"stems\":{\"zzz\":9,\"yyy\":0},\"allow\":[\"OK\"]}");
		Assert.Equal(3, list.Words["aaa"]);
		Assert.Equal(2, list.Words["bbb"]);
		Assert.Equal(0, list.Words["ccc"]); // mild words score nothing
		Assert.Equal(3, list.Stems["zzz"]); // clamped
		Assert.Equal(1, list.Stems["yyy"]);
		Assert.Contains("ok", list.Allow);
	}

	[Fact]
	public void TheBuiltInListHasTheExpectedShape()
	{
		WordList list = WordList.Default();
		Assert.Equal(3, list.Words["fuck"]);
		Assert.Equal(3, list.Words["shit"]);
		Assert.Equal(3, list.Words["bitch"]);
		Assert.Equal(2, list.Words["piss"]);
		Assert.Equal(2, list.Words["ass"]);
		Assert.Equal(0, list.Words["pissed"]);
		Assert.Equal(0, list.Words["damn"]);
		Assert.Equal(0, list.Words["hell"]);
		Assert.Equal(3, list.Stems["shit"]);
		Assert.Contains("fuck", list.Stems.Keys);
		Assert.Contains("scunthorpe", list.Allow);
		// every allowlisted word must be harmless: nothing on the list may also be a list word
		Assert.DoesNotContain(list.Allow, (string a) => list.Words.ContainsKey(a));
	}

	[Fact]
	public void MissingPartsAreFine()
	{
		WordList list = WordList.Parse("{}");
		Assert.Empty(list.Words);
		Assert.Empty(list.Stems);
		Assert.Empty(list.Allow);
	}
}
