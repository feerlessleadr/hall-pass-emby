using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class AlbumSelectionTests
{
	private static readonly DecisionSettings Balanced = DecisionSettings.Balanced;

	private static TrackRecord Track(long id, string artist, string album, string name, params EvidenceItem[] evidence)
	{
		return new TrackRecord
		{
			ItemId = id,
			Artist = artist,
			Album = album,
			Name = name,
			Path = "/music/" + artist + "/" + album + "/" + name + ".mp3",
			FileKey = "k" + id,
			EvidenceVersion = "v",
			LastScannedUtc = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
			Evidence = evidence.ToList(),
		};
	}

	private static EvidenceItem Explicit() => new EvidenceItem("lyrics", Verdict.Explicit, 0.75, "x");

	private static EvidenceItem Clean() => new EvidenceItem("lyrics", Verdict.Clean, 0.5, "x");

	private static EvidenceStore Library()
	{
		EvidenceStore s = new EvidenceStore();
		s.Upsert(Track(1, "Abe", "Alpha", "a1", Explicit()), Balanced);
		s.Upsert(Track(2, "Abe", "Alpha", "a2", Clean()), Balanced);
		s.Upsert(Track(3, "Abe", "Alpha", "a3"), Balanced); // no evidence: review
		s.Upsert(Track(4, "Abe", "Beta", "b1", Clean()), Balanced);
		s.Upsert(Track(5, "Zed", "alpha", "z1", Explicit()), Balanced); // another artist, same album title
		s.Upsert(Track(6, "Zed", "", "single"), Balanced); // no album
		return s;
	}

	[Fact]
	public void AnArtistAndAlbumFilterPicksOneAlbumIgnoringCase()
	{
		EvidenceStore s = Library();
		Assert.Equal(new long[] { 1, 2, 3 }, s.Query(new ReviewQuery { Artist = "abe", Album = "ALPHA" }).Items.Select(r => r.ItemId).OrderBy(i => i).ToArray());
		Assert.Equal(new long[] { 5 }, s.Query(new ReviewQuery { Artist = "Zed", Album = "Alpha" }).Items.Select(r => r.ItemId).ToArray());
		Assert.Equal(new long[] { 1, 2, 3, 4 }, s.Query(new ReviewQuery { Artist = "Abe" }).Items.Select(r => r.ItemId).OrderBy(i => i).ToArray());
		Assert.Equal(0, s.Query(new ReviewQuery { Artist = "Nobody" }).Total);
	}

	[Fact]
	public void AnEmptyAlbumMeansTracksWithNone()
	{
		EvidenceStore s = Library();
		Assert.Equal(new long[] { 6 }, s.Query(new ReviewQuery { Artist = "Zed", Album = "" }).Items.Select(r => r.ItemId).ToArray());
	}

	[Fact]
	public void AlbumsAreGroupedByArtistAndAlbumAndSorted()
	{
		AlbumGroupResult r = Library().Albums(new ReviewQuery());
		Assert.Equal(4, r.Total);
		Assert.Equal(4, r.Items.Count);
		Assert.Equal("Abe", r.Items[0].Artist);
		Assert.Equal("Alpha", r.Items[0].Album);
		Assert.Equal(3, r.Items[0].Matching);
		Assert.Equal(3, r.Items[0].Total);
		Assert.Equal(1, r.Items[0].ByDecision["Explicit"]);
		Assert.Equal(1, r.Items[0].ByDecision["LikelyClean"]);
		Assert.Equal(1, r.Items[0].ByDecision["Review"]);
		// the same album title by another artist is another album
		Assert.Contains(r.Items, g => g.Artist == "Zed" && g.Album == "alpha" && g.Total == 1);
	}

	[Fact]
	public void TheFiltersDecideWhichTracksCountButTheTotalIsTheWholeAlbum()
	{
		EvidenceStore s = Library();
		AlbumGroupResult r = s.Albums(new ReviewQuery { Decisions = new[] { Decision.Review } });
		AlbumGroup alpha = Assert.Single(r.Items, g => g.Artist == "Abe" && g.Album == "Alpha");
		Assert.Equal(1, alpha.Matching);
		Assert.Equal(3, alpha.Total);
		// albums with no matching track are left out
		Assert.DoesNotContain(r.Items, g => g.Album == "Beta");
	}

	[Fact]
	public void AlbumsArePaged()
	{
		EvidenceStore s = Library();
		AlbumGroupResult page = s.Albums(new ReviewQuery { Skip = 1, Take = 2 });
		Assert.Equal(4, page.Total);
		Assert.Equal(2, page.Items.Count);
		Assert.Equal("Beta", page.Items[0].Album);
		Assert.Empty(s.Albums(new ReviewQuery { Skip = 99 }).Items);
	}

	[Fact]
	public void MatchingIdsListEveryMatchAcrossPages()
	{
		EvidenceStore s = new EvidenceStore();
		for (long i = 1; i <= 120; i++)
		{
			s.Upsert(Track(i, "Band", "Album " + (i % 3), "t" + i, i % 2 == 0 ? Explicit() : Clean()), Balanced);
		}
		IReadOnlyList<long> ids = s.MatchingIds(new ReviewQuery { Decisions = new[] { Decision.Explicit } }, 1000, out bool truncated);
		Assert.Equal(60, ids.Count);
		Assert.False(truncated);
		Assert.All(ids, id => Assert.Equal(0, id % 2));
		// the page size of a query does not limit it
		Assert.Equal(60, s.MatchingIds(new ReviewQuery { Decisions = new[] { Decision.Explicit }, Take = 10, Skip = 20 }, 1000, out _).Count);
	}

	[Fact]
	public void MatchingIdsAreCappedAndSaySo()
	{
		EvidenceStore s = new EvidenceStore();
		for (long i = 1; i <= 30; i++)
		{
			s.Upsert(Track(i, "Band", "Album", "t" + i), Balanced);
		}
		IReadOnlyList<long> ids = s.MatchingIds(new ReviewQuery(), 10, out bool truncated);
		Assert.Equal(10, ids.Count);
		Assert.True(truncated);
		Assert.Empty(s.MatchingIds(new ReviewQuery { Text = "nothing like it" }, 10, out bool t2));
		Assert.False(t2);
	}

	[Fact]
	public void SettingAnOverrideOnAWholeAlbumWorksThroughTheMatchingIds()
	{
		EvidenceStore s = Library();
		IReadOnlyList<long> album = s.MatchingIds(new ReviewQuery { Artist = "Abe", Album = "Alpha" }, 1000, out _);
		Assert.Equal(3, s.SetOverride(album, ManualOverride.Explicit, Balanced));
		Assert.All(s.Query(new ReviewQuery { Artist = "Abe", Album = "Alpha" }).Items, r => Assert.Equal(Decision.Explicit, r.Decision));
		Assert.Equal(Decision.LikelyClean, s.Get(4)!.Decision); // another album: untouched
	}
}
