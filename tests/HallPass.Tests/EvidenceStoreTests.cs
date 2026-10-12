using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class EvidenceStoreTests
{
	private static readonly DecisionSettings Balanced = DecisionSettings.Balanced;

	private static TrackRecord Track(long id, string name = "Song", string artist = "Artist", string album = "Album", params EvidenceItem[] evidence)
	{
		return new TrackRecord
		{
			ItemId = id,
			Name = name,
			Artist = artist,
			Album = album,
			Path = "/music/" + artist + "/" + album + "/" + name + ".mp3",
			FileKey = "key" + id,
			EvidenceVersion = "v1",
			LastScannedUtc = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc),
			Evidence = evidence.ToList(),
		};
	}

	private static EvidenceItem Explicit(double c, string source = "lyrics") => new EvidenceItem(source, Verdict.Explicit, c, "x");

	private static EvidenceItem Clean(double c, string source = "lyrics") => new EvidenceItem(source, Verdict.Clean, c, "x");

	[Fact]
	public void UpsertWorksOutTheDecision()
	{
		EvidenceStore store = new EvidenceStore();
		Assert.Equal(Decision.Explicit, store.Upsert(Track(1, evidence: Explicit(0.75)), Balanced).Decision);
		Assert.Equal(Decision.LikelyClean, store.Upsert(Track(2, evidence: Clean(0.5)), Balanced).Decision);
		TrackRecord none = store.Upsert(Track(3), Balanced);
		Assert.Equal(Decision.Review, none.Decision);
		Assert.Equal(ReviewReason.NoEvidence, none.ReviewReason);
		Assert.Equal(3, store.Count);
	}

	[Fact]
	public void SkippedTracksStaySkipped()
	{
		EvidenceStore store = new EvidenceStore();
		TrackRecord t = Track(1, evidence: Explicit(0.9));
		t.Skipped = true;
		Assert.Equal(Decision.Skipped, store.Upsert(t, Balanced).Decision);
	}

	[Fact]
	public void AManualOverrideSurvivesARescan()
	{
		EvidenceStore store = new EvidenceStore();
		store.Upsert(Track(1, evidence: Explicit(0.75)), Balanced);
		Assert.Equal(1, store.SetOverride(new long[] { 1 }, ManualOverride.Clean, Balanced));
		Assert.Equal(Decision.Clean, store.Get(1)!.Decision);

		// a rescan brings new evidence for the same track: the override stays and still wins
		TrackRecord rescanned = store.Upsert(Track(1, evidence: Explicit(0.95)), Balanced);
		Assert.Equal(ManualOverride.Clean, rescanned.Override);
		Assert.Equal(Decision.Clean, rescanned.Decision);
		Assert.Equal(1.0, rescanned.Confidence);

		// clearing the override goes back to what the evidence says
		store.SetOverride(new long[] { 1 }, ManualOverride.None, Balanced);
		Assert.Equal(Decision.Explicit, store.Get(1)!.Decision);
	}

	[Fact]
	public void SettingOverridesCountsOnlyKnownTracksOnce()
	{
		EvidenceStore store = new EvidenceStore();
		store.Upsert(Track(1), Balanced);
		store.Upsert(Track(2), Balanced);
		Assert.Equal(2, store.SetOverride(new long[] { 1, 2, 2, 99 }, ManualOverride.Explicit, Balanced));
		Assert.All(store.All(), (TrackRecord r) => Assert.Equal(Decision.Explicit, r.Decision));
	}

	[Fact]
	public void RedecideAllUsesNewThresholdsWithoutRescanning()
	{
		EvidenceStore store = new EvidenceStore();
		store.Upsert(Track(1, evidence: Explicit(0.8)), Balanced);
		Assert.Equal(Decision.Explicit, store.Get(1)!.Decision);
		store.RedecideAll(DecisionSettings.Relaxed); // explicit needs 0.85 there
		Assert.Equal(Decision.Review, store.Get(1)!.Decision);
		Assert.Equal(ReviewReason.Suspected, store.Get(1)!.ReviewReason);
	}

	[Fact]
	public void TheStoreHandsOutCopies()
	{
		EvidenceStore store = new EvidenceStore();
		store.Upsert(Track(1, evidence: Explicit(0.75)), Balanced);
		TrackRecord copy = store.Get(1)!;
		copy.Decision = Decision.Clean;
		copy.Evidence.Clear();
		Assert.Equal(Decision.Explicit, store.Get(1)!.Decision);
		Assert.Single(store.Get(1)!.Evidence);
		Assert.Null(store.Get(404));
	}

	private static EvidenceStore Library()
	{
		EvidenceStore store = new EvidenceStore();
		store.Upsert(Track(1, "Alpha", "Zed", "Z1", Explicit(0.75)), Balanced);
		store.Upsert(Track(2, "Bravo", "Abe", "A1", Clean(0.5)), Balanced);
		store.Upsert(Track(3, "Charlie", "Abe", "A1", Explicit(0.5, "title")), Balanced);
		store.Upsert(Track(4, "Delta", "Abe", "A2", Explicit(0.6, "folder"), Clean(0.5)), Balanced);
		store.Upsert(Track(5, "Echo", "Mid", "M1"), Balanced);
		store.SetOverride(new long[] { 5 }, ManualOverride.Clean, Balanced);
		return store;
	}

	[Fact]
	public void QueryFiltersByDecisionAndReason()
	{
		EvidenceStore store = Library();
		Assert.Equal(new long[] { 1 }, store.Query(new ReviewQuery { Decisions = new[] { Decision.Explicit } }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(2, store.Query(new ReviewQuery { Decisions = new[] { Decision.Review } }).Total);
		Assert.Equal(new long[] { 3 }, store.Query(new ReviewQuery { Reasons = new[] { ReviewReason.Suspected } }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(new long[] { 4 }, store.Query(new ReviewQuery { Reasons = new[] { ReviewReason.Conflict } }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
	}

	[Fact]
	public void QueryFiltersByTextSourceAndOverride()
	{
		EvidenceStore store = Library();
		Assert.Equal(new long[] { 2, 3, 4 }, store.Query(new ReviewQuery { Text = "abe" }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(new long[] { 3 }, store.Query(new ReviewQuery { Text = "CHARLIE" }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(new long[] { 4 }, store.Query(new ReviewQuery { Source = "folder" }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(new long[] { 5 }, store.Query(new ReviewQuery { OverridesOnly = true }).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(0, store.Query(new ReviewQuery { Text = "nothing like this" }).Total);
	}

	[Fact]
	public void QuerySortsAndPages()
	{
		EvidenceStore store = Library();
		// artist, album, title: Abe/A1/Bravo, Abe/A1/Charlie, Abe/A2/Delta, Mid, Zed
		Assert.Equal(new long[] { 2, 3, 4, 5, 1 }, store.Query(new ReviewQuery()).Items.Select((TrackRecord r) => r.ItemId).ToArray());
		QueryResult page = store.Query(new ReviewQuery { Skip = 1, Take = 2 });
		Assert.Equal(5, page.Total);
		Assert.Equal(new long[] { 3, 4 }, page.Items.Select((TrackRecord r) => r.ItemId).ToArray());
		Assert.Equal(1L, store.Query(new ReviewQuery { SortByConfidence = true }).Items.First(r => r.Confidence >= 0.75 && r.Override == ManualOverride.None).ItemId);
		Assert.Empty(store.Query(new ReviewQuery { Skip = 100 }).Items);
	}

	[Fact]
	public void StatsCountDecisionsReasonsAndOverrides()
	{
		StoreStats stats = Library().Stats();
		Assert.Equal(5, stats.Total);
		Assert.Equal(1, stats.ByDecision["Explicit"]);
		Assert.Equal(1, stats.ByDecision["LikelyClean"]);
		Assert.Equal(2, stats.ByDecision["Review"]);
		Assert.Equal(1, stats.ByDecision["Clean"]);
		Assert.Equal(1, stats.ByReason["Suspected"]);
		Assert.Equal(1, stats.ByReason["Conflict"]);
		Assert.Equal(1, stats.Overrides);
		Assert.Equal(new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc), stats.LastScanUtc);
		Assert.Null(new EvidenceStore().Stats().LastScanUtc);
	}

	[Fact]
	public void TracksLeftOutAreForgotten()
	{
		EvidenceStore store = Library();
		Assert.Equal(2, store.RemoveAllExcept(new HashSet<long> { 1, 2, 3 }));
		Assert.Equal(3, store.Count);
		Assert.Null(store.Get(4));
	}

	[Fact]
	public void JsonRoundTripKeepsEverything()
	{
		EvidenceStore store = Library();
		string json = store.ToJson();
		Assert.Contains("\"Decision\":\"Explicit\"", json);
		Assert.DoesNotContain("lyric text", json);
		EvidenceStore back = EvidenceStore.FromJson(json);
		Assert.Equal(store.Count, back.Count);
		TrackRecord a = store.Get(4)!;
		TrackRecord b = back.Get(4)!;
		Assert.Equal(a.Decision, b.Decision);
		Assert.Equal(a.ReviewReason, b.ReviewReason);
		Assert.Equal(a.Evidence.Select((EvidenceItem e) => e.Source + e.Verdict + e.Confidence).ToArray(), b.Evidence.Select((EvidenceItem e) => e.Source + e.Verdict + e.Confidence).ToArray());
		Assert.Equal(ManualOverride.Clean, back.Get(5)!.Override);
		Assert.Equal(a.LastScannedUtc, b.LastScannedUtc);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("{ this is not json")]
	[InlineData("[]")]
	public void EmptyOrDamagedFilesGiveAnEmptyStore(string? text)
	{
		Assert.Equal(0, EvidenceStore.FromJson(text).Count);
	}

	[Fact]
	public void RescanRules()
	{
		TrackRecord known = Track(1);
		Assert.True(EvidenceStore.NeedsRescan(null, "key1", "v1", false));
		Assert.False(EvidenceStore.NeedsRescan(known, "key1", "v1", false));
		Assert.True(EvidenceStore.NeedsRescan(known, "key1", "v1", true));
		Assert.True(EvidenceStore.NeedsRescan(known, "other", "v1", false));
		Assert.True(EvidenceStore.NeedsRescan(known, "key1", "v2", false));
	}

	[Fact]
	public void FingerprintsAreStableAndSensitive()
	{
		string a = EvidenceStore.Fingerprint("x", "y", null);
		Assert.Equal(a, EvidenceStore.Fingerprint("x", "y", null));
		Assert.NotEqual(a, EvidenceStore.Fingerprint("x", "z", null));
		Assert.NotEqual(EvidenceStore.Fingerprint("ab", "c"), EvidenceStore.Fingerprint("a", "bc"));
		Assert.Equal(16, a.Length);
	}

	[Fact]
	public void TheStoreIsSafeToUseFromSeveralThreads()
	{
		EvidenceStore store = new EvidenceStore();
		System.Threading.Tasks.Parallel.For(0, 400, (int i) =>
		{
			store.Upsert(Track(i % 50, evidence: Explicit(0.75)), Balanced);
			store.SetOverride(new long[] { i % 50 }, i % 2 == 0 ? ManualOverride.Clean : ManualOverride.None, Balanced);
			store.Query(new ReviewQuery { Take = 10 });
			store.Stats();
		});
		Assert.Equal(50, store.Count);
	}
}
