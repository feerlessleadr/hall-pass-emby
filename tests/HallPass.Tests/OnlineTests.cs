using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class EmbeddedTagsTests
{
	private static IReadOnlyDictionary<string, string> Tags(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

	[Theory]
	[InlineData("itunesadvisory", "1", Verdict.Explicit, 1.0)]
	[InlineData("itunesadvisory", "4", Verdict.Explicit, 1.0)]
	[InlineData("itunesadvisory", "2", Verdict.Clean, 0.9)]
	[InlineData("rtng", "1", Verdict.Explicit, 1.0)]
	[InlineData("rtng", "2", Verdict.Clean, 0.9)]
	[InlineData("explicit", "true", Verdict.Explicit, 1.0)]
	[InlineData("explicit", "Yes", Verdict.Explicit, 1.0)]
	[InlineData("explicit", "1", Verdict.Explicit, 1.0)]
	[InlineData("explicit", "0", Verdict.Clean, 0.7)]
	[InlineData("explicit", "false", Verdict.Clean, 0.7)]
	[InlineData("contentadvisory", "Explicit", Verdict.Explicit, 1.0)]
	[InlineData("contentadvisory", "clean", Verdict.Clean, 0.9)]
	[InlineData("parental_advisory", "explicit", Verdict.Explicit, 1.0)]
	public void AdvisoryTagsBecomeEvidence(string key, string value, Verdict verdict, double confidence)
	{
		EvidenceItem e = Assert.Single(EmbeddedTags.Evaluate(Tags((key, value))));
		Assert.Equal(verdict, e.Verdict);
		Assert.Equal(confidence, e.Confidence);
		Assert.Equal("embedded", e.Source);
		Assert.Contains(key, e.Detail);
	}

	[Theory]
	[InlineData("itunesadvisory", "0")]
	[InlineData("itunesadvisory", "")]
	[InlineData("itunesadvisory", "abc")]
	[InlineData("explicit", "maybe")]
	[InlineData("rating", "80")]
	[InlineData("title", "explicit")]
	public void NothingDefiniteGivesNoEvidence(string key, string value)
	{
		Assert.Empty(EmbeddedTags.Evaluate(Tags((key, value))));
	}

	[Fact]
	public void TheStrongestOfEachVerdictIsKept()
	{
		IReadOnlyList<EvidenceItem> items = EmbeddedTags.Evaluate(Tags(("itunesadvisory", "2"), ("explicit", "0"), ("contentadvisory", "explicit")));
		Assert.Equal(2, items.Count);
		Assert.Equal(1.0, items.Single(i => i.Verdict == Verdict.Explicit).Confidence);
		Assert.Equal(0.9, items.Single(i => i.Verdict == Verdict.Clean).Confidence);
	}

	[Fact]
	public void TheRtngAtomValueMapsLikeTheTag()
	{
		Assert.Equal(Verdict.Explicit, EmbeddedTags.FromRtngAtom(1)!.Verdict);
		Assert.Equal(Verdict.Explicit, EmbeddedTags.FromRtngAtom(4)!.Verdict);
		Assert.Equal(Verdict.Clean, EmbeddedTags.FromRtngAtom(2)!.Verdict);
		Assert.Null(EmbeddedTags.FromRtngAtom(0));
		Assert.Null(EmbeddedTags.FromRtngAtom(null));
		Assert.Equal(Verdict.None, EmbeddedTags.FromRtngAtom(9)!.Verdict);
	}

	[Fact]
	public void IdentifiersComeFromIsrcAndBarcodeTags()
	{
		TrackIdentifiers a = EmbeddedTags.Identifiers(Tags(("isrc", "US-UG1-25-03230"), ("barcode", "008811162429")));
		Assert.Equal("USUG12503230", a.Isrc);
		Assert.Equal("008811162429", a.Upc);
		// an MP3 stores it in a TSRC frame; several values: the first valid one
		TrackIdentifiers b = EmbeddedTags.Identifiers(Tags(("tsrc", "junk;USAM19400007 USAM10200572"), ("upc", "0602537875580"), ("barcode", "12")));
		Assert.Equal("USAM19400007", b.Isrc);
		Assert.Equal("0602537875580", b.Upc);
		TrackIdentifiers none = EmbeddedTags.Identifiers(Tags(("isrc", "123"), ("barcode", "abc")));
		Assert.Null(none.Isrc);
		Assert.Null(none.Upc);
		Assert.Null(EmbeddedTags.Identifiers(Tags()).Isrc);
	}

	[Fact]
	public void ProbeOutputIsReadIntoOneLowerCaseDictionary()
	{
		string json = """{"streams":[{"tags":{"Language":"eng"}}],"format":{"tags":{"ITUNESADVISORY":"1","TSRC":"USAM19400007","Title":"Song"}}}""";
		IReadOnlyDictionary<string, string> tags = EmbeddedTags.ParseProbeJson(json);
		Assert.Equal("1", tags["itunesadvisory"]);
		Assert.Equal("USAM19400007", tags["tsrc"]);
		Assert.Equal("eng", tags["language"]);
		Assert.Empty(EmbeddedTags.ParseProbeJson(null));
		Assert.Empty(EmbeddedTags.ParseProbeJson("not json"));
		Assert.Empty(EmbeddedTags.ParseProbeJson("{\"format\":{}}"));
	}

	private static byte[] RtngAtom(byte value, int paddingBefore = 10)
	{
		List<byte> bytes = new List<byte>(new byte[paddingBefore]);
		bytes.AddRange(new byte[] { 0, 0, 0, 26 });
		bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("rtng"));
		bytes.AddRange(new byte[] { 0, 0, 0, 18 });
		bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("data"));
		bytes.AddRange(new byte[] { 0, 0, 0, 21, 0, 0, 0, 0, value });
		bytes.AddRange(new byte[5]);
		return bytes.ToArray();
	}

	[Theory]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(4)]
	[InlineData(0)]
	public void TheRtngAtomIsFoundInFileBytes(byte value)
	{
		Assert.Equal(value, EmbeddedTags.FindRtngAtom(RtngAtom(value)));
	}

	[Fact]
	public void TheRtngSearchIgnoresLookAlikesAndEmptyData()
	{
		Assert.Null(EmbeddedTags.FindRtngAtom(new byte[0]));
		Assert.Null(EmbeddedTags.FindRtngAtom(new byte[100]));
		// the letters 'rtng' without a data atom after them are not the atom
		byte[] fake = System.Text.Encoding.ASCII.GetBytes("xxxxrtngxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx");
		Assert.Null(EmbeddedTags.FindRtngAtom(fake));
		// a look-alike first, the real one later
		byte[] both = fake.Concat(RtngAtom(2)).ToArray();
		Assert.Equal(2, EmbeddedTags.FindRtngAtom(both));
		// cut off in the middle of the atom
		Assert.Null(EmbeddedTags.FindRtngAtom(RtngAtom(1).Take(14).ToArray()));
	}
}

public class ProviderParserTests
{
	private const string DeezerExplicit = """{"id":1,"readable":true,"title":"Come Pick Me Up","isrc":"USUG12503230","duration":295,"explicit_lyrics":true,"explicit_content_lyrics":1,"explicit_content_cover":0,"artist":{"id":9,"name":"Machine Gun Kelly"},"album":{"id":8,"title":"Come Pick Me Up"},"type":"track"}""";

	private const string DeezerClean = """{"id":2,"title":"Voyeur","isrc":"USCR29752441","duration":163,"explicit_lyrics":false,"explicit_content_lyrics":0,"artist":{"name":"blink-182"},"album":{"title":"Dude Ranch"}}""";

	[Fact]
	public void DeezerTrackAnswers()
	{
		ProviderParse e = DeezerParser.ParseTrack(DeezerExplicit);
		Assert.Equal(ProviderStatus.Ok, e.Status);
		CandidateTrack t = Assert.Single(e.Candidates);
		Assert.Equal("Come Pick Me Up", t.Title);
		Assert.Equal("Machine Gun Kelly", t.Artist);
		Assert.Equal("Come Pick Me Up", t.Album);
		Assert.Equal(295, t.DurationSeconds);
		Assert.Equal("USUG12503230", t.Isrc);
		Assert.Equal(ExplicitFlag.Explicit, t.Flag);
		Assert.Equal("deezer", t.Source);
		Assert.Equal(ExplicitFlag.Clean, Assert.Single(DeezerParser.ParseTrack(DeezerClean).Candidates).Flag);
	}

	[Theory]
	[InlineData(0, ExplicitFlag.Clean)]
	[InlineData(1, ExplicitFlag.Explicit)]
	[InlineData(2, ExplicitFlag.Unknown)]
	[InlineData(3, ExplicitFlag.Clean)]
	[InlineData(4, ExplicitFlag.Unknown)]
	[InlineData(5, ExplicitFlag.Unknown)]
	public void DeezerExplicitCodes(int code, ExplicitFlag expected)
	{
		Assert.Equal(expected, DeezerParser.FlagFrom(code, null));
		Assert.Equal(expected, DeezerParser.FlagFrom(code, true)); // the code wins over the boolean
	}

	[Fact]
	public void WithoutTheCodeOnlyTrueCountsAndFalseIsNotProofOfClean()
	{
		Assert.Equal(ExplicitFlag.Explicit, DeezerParser.FlagFrom(null, true));
		Assert.Equal(ExplicitFlag.Unknown, DeezerParser.FlagFrom(null, false));
		Assert.Equal(ExplicitFlag.Unknown, DeezerParser.FlagFrom(null, null));
	}

	[Fact]
	public void DeezerErrorsAreTold()
	{
		Assert.Equal(ProviderStatus.NotFound, DeezerParser.ParseTrack("""{"error":{"type":"DataException","message":"no data","code":800}}""").Status);
		ProviderParse quota = DeezerParser.ParseTrack("""{"error":{"type":"Exception","message":"Quota limit exceeded","code":4}}""");
		Assert.Equal(ProviderStatus.Retry, quota.Status);
		Assert.Contains("Quota", quota.Message);
		Assert.Equal(ProviderStatus.Failed, DeezerParser.ParseTrack("""{"error":{"code":300,"message":"x"}}""").Status);
		Assert.Equal(ProviderStatus.Failed, DeezerParser.ParseTrack("").Status);
		Assert.Equal(ProviderStatus.Failed, DeezerParser.ParseTrack("<html>").Status);
		Assert.Equal(ProviderStatus.Failed, DeezerParser.ParseTrack("[1]").Status);
		Assert.Equal(ProviderStatus.NotFound, DeezerParser.ParseTrack("{}").Status);
	}

	[Fact]
	public void DeezerSearchListsTracksAndSkipsJunk()
	{
		string json = """{"data":[{"title":"Killing In The Name","duration":313,"explicit_lyrics":true,"explicit_content_lyrics":1,"artist":{"name":"Rage Against the Machine"},"album":{"title":"Rage Against The Machine"}},{"notATrack":true},{"title":"Killing in the Name (Live)","duration":300,"explicit_lyrics":true,"artist":{"name":"Rage Against the Machine"}}],"total":3}""";
		ProviderParse p = DeezerParser.ParseList(json);
		Assert.Equal(2, p.Candidates.Count);
		Assert.All(p.Candidates, (CandidateTrack c) => Assert.Equal(ExplicitFlag.Explicit, c.Flag));
		Assert.Null(p.Candidates[1].Album);
		Assert.Equal(ProviderStatus.NotFound, DeezerParser.ParseList("""{"data":[],"total":0}""").Status);
	}

	private const string ITunesAlbum = """{"resultCount":4,"results":[{"wrapperType":"collection","collectionName":"Dude Ranch","artistName":"blink-182"},{"wrapperType":"track","kind":"song","artistName":"blink-182","collectionName":"Dude Ranch","trackName":"Pathetic","trackTimeMillis":170000,"trackExplicitness":"explicit"},{"wrapperType":"track","kind":"song","artistName":"blink-182","collectionName":"Dude Ranch","trackName":"Voyeur","trackTimeMillis":163000,"trackExplicitness":"notExplicit"},{"wrapperType":"track","artistName":"blink-182","collectionName":"Dude Ranch","trackName":"Edited","trackTimeMillis":1000,"trackExplicitness":"cleaned"}]}""";

	[Fact]
	public void ITunesAlbumLookupGivesTheSongsOnly()
	{
		ProviderParse p = ITunesParser.ParseLookup(ITunesAlbum);
		Assert.Equal(ProviderStatus.Ok, p.Status);
		Assert.Equal(new[] { "Pathetic", "Voyeur", "Edited" }, p.Candidates.Select(c => c.Title).ToArray());
		Assert.Equal(new[] { ExplicitFlag.Explicit, ExplicitFlag.Clean, ExplicitFlag.Clean }, p.Candidates.Select(c => c.Flag).ToArray());
		Assert.Equal(163, p.Candidates[1].DurationSeconds);
		Assert.Equal("Dude Ranch", p.Candidates[0].Album);
		Assert.All(p.Candidates, (CandidateTrack c) => Assert.Equal("itunes", c.Source));
		Assert.Equal(ProviderStatus.NotFound, ITunesParser.ParseLookup("""{"resultCount":0,"results":[]}""").Status);
		Assert.Equal(ProviderStatus.Failed, ITunesParser.ParseLookup("nope").Status);
		Assert.Equal(ProviderStatus.Failed, ITunesParser.ParseLookup("{}").Status);
	}

	[Theory]
	[InlineData("explicit", ExplicitFlag.Explicit)]
	[InlineData("cleaned", ExplicitFlag.Clean)]
	[InlineData("notExplicit", ExplicitFlag.Clean)]
	[InlineData("", ExplicitFlag.Unknown)]
	[InlineData("something", ExplicitFlag.Unknown)]
	[InlineData(null, ExplicitFlag.Unknown)]
	public void ITunesExplicitness(string? text, ExplicitFlag expected)
	{
		Assert.Equal(expected, ITunesParser.FlagFrom(text));
	}
}

public class OnlineMatcherTests
{
	private static readonly TrackInfo Local = new TrackInfo("Come Pick Me Up", "Machine Gun Kelly", "Come Pick Me Up", 295, "USUG12503230");

	private static CandidateTrack C(string title, double? seconds, ExplicitFlag flag, string artist = "Machine Gun Kelly", string? isrc = null, string? album = "Come Pick Me Up", string source = "deezer")
		=> new CandidateTrack(title, artist, album, seconds, isrc, flag, source);

	[Fact]
	public void AnIsrcMatchOfAnExplicitTrackIsStrongExplicitEvidence()
	{
		EvidenceItem e = OnlineMatcher.Evaluate(Local, new[] { C("Whatever", 100, ExplicitFlag.Explicit, isrc: "USUG12503230") }, "deezer")!;
		Assert.Equal(Verdict.Explicit, e.Verdict);
		Assert.Equal(TrackMatcher.IsrcMatch, e.Confidence);
		Assert.Equal("deezer", e.Source);
		Assert.Contains("ISRC", e.Detail);
	}

	[Fact]
	public void AnOnlineNotExplicitIsCappedBelowTheAutomaticCleanBar()
	{
		EvidenceItem e = OnlineMatcher.Evaluate(Local, new[] { C("x", 1, ExplicitFlag.Clean, isrc: "USUG12503230") }, "deezer")!;
		Assert.Equal(Verdict.Clean, e.Verdict);
		Assert.Equal(OnlineMatcher.CleanCap, e.Confidence);
		Assert.True(e.Confidence < DecisionSettings.Balanced.AutoClean);
	}

	[Fact]
	public void TitleArtistAndLengthMatchWithoutAnIsrc()
	{
		TrackInfo noIsrc = Local with { Isrc = null };
		EvidenceItem e = OnlineMatcher.Evaluate(noIsrc, new[] { C("Come Pick Me Up", 296, ExplicitFlag.Explicit) }, "deezer")!;
		Assert.Equal(TrackMatcher.TitleArtistDurationMatch + TrackMatcher.AlbumBonus, e.Confidence, 6);
	}

	[Fact]
	public void UnknownFlagsAndWeakMatchesGiveNothing()
	{
		TrackInfo noIsrc = Local with { Isrc = null };
		Assert.Null(OnlineMatcher.Evaluate(noIsrc, new[] { C("Come Pick Me Up", 295, ExplicitFlag.Unknown) }, "deezer"));
		Assert.Null(OnlineMatcher.Evaluate(noIsrc, new[] { C("Completely Different", 295, ExplicitFlag.Explicit) }, "deezer"));
		Assert.Null(OnlineMatcher.Evaluate(noIsrc, new[] { C("Come Pick Me Up", 295, ExplicitFlag.Explicit, artist: "Someone Else") }, "deezer"));
		// same song, another length (a live take or an edit): too weak to count
		Assert.Null(OnlineMatcher.Evaluate(noIsrc, new[] { C("Come Pick Me Up", 330, ExplicitFlag.Explicit) }, "deezer"));
		Assert.Null(OnlineMatcher.Evaluate(Local, Array.Empty<CandidateTrack>(), "deezer"));
	}

	[Fact]
	public void WhenEqualMatchesDisagreeTheExplicitOneWins()
	{
		TrackInfo noIsrc = Local with { Isrc = null };
		EvidenceItem e = OnlineMatcher.Evaluate(noIsrc, new[] { C("Come Pick Me Up", 295, ExplicitFlag.Clean), C("Come Pick Me Up", 295, ExplicitFlag.Explicit) }, "deezer")!;
		Assert.Equal(Verdict.Explicit, e.Verdict);
	}

	[Fact]
	public void AStrongExplicitMatchBeatsABetterCleanOne()
	{
		// the clean entry matches by ISRC (0.95), another entry with the same length says explicit (0.85): explicit still wins
		EvidenceItem e = OnlineMatcher.Evaluate(Local, new[] { C("x", 1, ExplicitFlag.Clean, isrc: "USUG12503230"), C("Come Pick Me Up", 295, ExplicitFlag.Explicit) }, "deezer")!;
		Assert.Equal(Verdict.Explicit, e.Verdict);
	}

	[Fact]
	public void TheBestMatchDecidesWhenNothingIsExplicit()
	{
		TrackInfo noIsrc = Local with { Isrc = null, Album = null };
		EvidenceItem e = OnlineMatcher.Evaluate(noIsrc, new[] { C("Come Pick Me Up", 295, ExplicitFlag.Clean, album: null), C("Come Pick Me Up (Live)", 300, ExplicitFlag.Unknown) }, "itunes")!;
		Assert.Equal(Verdict.Clean, e.Verdict);
		Assert.Equal("itunes", e.Source);
	}
}

public class TokenBucketTests
{
	[Fact]
	public void TheBurstGoesAtOnceThenRequestsAreSpacedOut()
	{
		long now = 0;
		TokenBucket bucket = new TokenBucket(3, 1.0, () => now); // 3 in a burst, then one per second
		Assert.Equal(TimeSpan.Zero, bucket.Reserve());
		Assert.Equal(TimeSpan.Zero, bucket.Reserve());
		Assert.Equal(TimeSpan.Zero, bucket.Reserve());
		Assert.Equal(TimeSpan.FromMilliseconds(1000), bucket.Reserve());
		Assert.Equal(TimeSpan.FromMilliseconds(2000), bucket.Reserve()); // queued behind the one before
	}

	[Fact]
	public void TimePassingFillsTheBucketUpToItsCapacity()
	{
		long now = 0;
		TokenBucket bucket = new TokenBucket(2, 2.0, () => now);
		bucket.Reserve();
		bucket.Reserve();
		now = 500; // one token back
		Assert.Equal(TimeSpan.Zero, bucket.Reserve());
		now = 100_000; // far longer than needed: still only the capacity
		Assert.Equal(TimeSpan.Zero, bucket.Reserve());
		Assert.Equal(TimeSpan.Zero, bucket.Reserve());
		Assert.True(bucket.Reserve() > TimeSpan.Zero);
	}

	[Fact]
	public void SlowRatesWaitLongEnough()
	{
		long now = 0;
		TokenBucket itunes = new TokenBucket(1, 20.0 / 60.0, () => now); // about 20 a minute
		itunes.Reserve();
		TimeSpan wait = itunes.Reserve();
		Assert.InRange(wait.TotalSeconds, 2.9, 3.1);
	}
}

public class OnlineCacheTests
{
	private static readonly DateTime Now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);

	private static CandidateTrack Track() => new CandidateTrack("Song", "Artist", "Album", 200, "USXXX0000001", ExplicitFlag.Explicit, "deezer");

	[Fact]
	public void FoundAnswersLastLongerThanEmptyOnes()
	{
		OnlineCache cache = new OnlineCache();
		cache.Put("found", new[] { Track() }, Now);
		cache.Put("empty", Array.Empty<CandidateTrack>(), Now);
		Assert.True(cache.TryGet("found", Now.AddDays(60), 90, 14, out CachedLookup f));
		Assert.Single(f.Candidates);
		Assert.True(cache.TryGet("empty", Now.AddDays(10), 90, 14, out CachedLookup e));
		Assert.False(e.Found);
		Assert.False(cache.TryGet("empty", Now.AddDays(15), 90, 14, out _));
		Assert.False(cache.TryGet("found", Now.AddDays(91), 90, 14, out _));
		Assert.False(cache.TryGet("never asked", Now, 90, 14, out _));
	}

	[Fact]
	public void AnAnswerIsReplacedByANewerOne()
	{
		OnlineCache cache = new OnlineCache();
		cache.Put("k", Array.Empty<CandidateTrack>(), Now);
		cache.Put("k", new[] { Track() }, Now.AddDays(20));
		Assert.True(cache.TryGet("k", Now.AddDays(21), 90, 14, out CachedLookup entry));
		Assert.True(entry.Found);
		Assert.Equal(1, cache.Count);
	}

	[Fact]
	public void PruneDropsOldEntriesOnly()
	{
		OnlineCache cache = new OnlineCache();
		cache.Put("old", new[] { Track() }, Now.AddDays(-200));
		cache.Put("new", new[] { Track() }, Now.AddDays(-5));
		Assert.Equal(1, cache.Prune(Now, 90));
		Assert.Equal(1, cache.Count);
		Assert.True(cache.TryGet("new", Now, 90, 14, out _));
	}

	[Fact]
	public void TheCacheSurvivesJson()
	{
		OnlineCache cache = new OnlineCache();
		cache.Put("deezer-isrc:USXXX0000001", new[] { Track() }, Now);
		string json = cache.ToJson();
		Assert.Contains("\"Flag\":\"Explicit\"", json);
		OnlineCache back = OnlineCache.FromJson(json);
		Assert.True(back.TryGet("deezer-isrc:USXXX0000001", Now, 90, 14, out CachedLookup e));
		CandidateTrack t = Assert.Single(e.Candidates);
		Assert.Equal(ExplicitFlag.Explicit, t.Flag);
		Assert.Equal("USXXX0000001", t.Isrc);
		Assert.Equal(200, t.DurationSeconds);
		Assert.Equal(0, OnlineCache.FromJson(null).Count);
		Assert.Equal(0, OnlineCache.FromJson("{ broken").Count);
		Assert.Equal(0, OnlineCache.FromJson("[]").Count);
	}
}

public class CorroboratedCleanTests
{
	private static DecisionResult Decide(DecisionSettings s, params EvidenceItem[] e) => DecisionEngine.Decide(ManualOverride.None, e, s);

	private static EvidenceItem Clean(string source, double c) => new EvidenceItem(source, Verdict.Clean, c, "");

	[Fact]
	public void TwoDifferentSourcesSayingCleanMakeACleanTrack()
	{
		DecisionResult r = Decide(DecisionSettings.Balanced, Clean("lyrics", 0.5), Clean("deezer", 0.85));
		Assert.Equal(Decision.Clean, r.Decision);
		Assert.Equal(0.85, r.Confidence);
	}

	[Fact]
	public void OneOnlineSourceAloneIsOnlyLikelyClean()
	{
		Assert.Equal(Decision.LikelyClean, Decide(DecisionSettings.Balanced, Clean("deezer", 0.85)).Decision);
		Assert.Equal(Decision.LikelyClean, Decide(DecisionSettings.Balanced, Clean("deezer", 0.85), Clean("deezer", 0.85)).Decision);
	}

	[Fact]
	public void TwoWeakSourcesAreNotEnough()
	{
		Assert.Equal(Decision.LikelyClean, Decide(DecisionSettings.Balanced, Clean("lyrics", 0.5), Clean("title", 0.6)).Decision);
	}

	[Fact]
	public void AnyExplicitEvidenceBlocksIt()
	{
		DecisionResult r = Decide(DecisionSettings.Balanced, Clean("lyrics", 0.5), Clean("deezer", 0.85), new EvidenceItem("title", Verdict.Explicit, 0.6, ""));
		Assert.NotEqual(Decision.Clean, r.Decision);
	}

	[Fact]
	public void ThePresetsAskForDifferentAmounts()
	{
		Assert.Equal(Decision.LikelyClean, Decide(DecisionSettings.Conservative, Clean("lyrics", 0.5), Clean("deezer", 0.85)).Decision); // needs 0.9
		Assert.Equal(Decision.Clean, Decide(DecisionSettings.Conservative, Clean("embedded", 0.9), Clean("deezer", 0.85)).Decision);
		Assert.Equal(Decision.Clean, Decide(DecisionSettings.Relaxed, Clean("lyrics", 0.5), Clean("deezer", 0.85)).Decision);
	}
}
