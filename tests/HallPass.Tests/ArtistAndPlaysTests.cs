using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class ArtistAndPlaysTests
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
			LastScannedUtc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
			Evidence = evidence.ToList(),
		};
	}

	private static EvidenceStore Library()
	{
		EvidenceStore s = new EvidenceStore();
		s.Upsert(Track(1, "Abe", "Alpha", "a1"), Balanced);
		s.Upsert(Track(2, "Abe", "Alpha", "a2"), Balanced);
		s.Upsert(Track(3, "Abe", "Beta", "b1"), Balanced);
		s.Upsert(Track(4, "Zed", "Zeta", "z1"), Balanced);
		s.Upsert(Track(5, "zed", "Zeta", "z2"), Balanced); // same artist, other case
		s.Upsert(Track(6, "Mia", "", "single"), Balanced);
		return s;
	}

	[Fact]
	public void ArtistsAreGroupedIgnoringCaseWithTheirAlbumCounts()
	{
		ArtistGroupResult r = Library().Artists(new ReviewQuery());
		Assert.Equal(3, r.Total);
		Assert.Equal(new[] { "Abe", "Mia", "Zed" }, r.Items.Select(a => a.Artist).ToArray());
		ArtistGroup abe = r.Items[0];
		Assert.Equal(3, abe.Matching);
		Assert.Equal(3, abe.Total);
		Assert.Equal(2, abe.Albums);
		Assert.Equal(2, r.Items[2].Matching); // "Zed" and "zed" are one artist
	}

	[Fact]
	public void TheFiltersDecideWhoIsListedButTheTotalIsEveryTrack()
	{
		EvidenceStore s = Library();
		s.SetOverride(new long[] { 1 }, ManualOverride.Explicit, Balanced);
		ArtistGroupResult r = s.Artists(new ReviewQuery { Decisions = new[] { Decision.Explicit } });
		ArtistGroup abe = Assert.Single(r.Items);
		Assert.Equal(1, abe.Matching);
		Assert.Equal(3, abe.Total);
	}

	[Fact]
	public void ArtistsArePaged()
	{
		ArtistGroupResult r = Library().Artists(new ReviewQuery { Skip = 1, Take = 1 });
		Assert.Equal(3, r.Total);
		Assert.Equal("Mia", Assert.Single(r.Items).Artist);
	}

	[Fact]
	public void TracksCanBeOrderedMostPlayedFirst()
	{
		EvidenceStore s = Library();
		Dictionary<long, int> plays = new Dictionary<long, int> { [3] = 40, [5] = 7, [1] = 7 };
		IReadOnlyList<TrackRecord> items = s.Query(new ReviewQuery { SortByPlays = true, PlayCounts = plays }).Items;
		Assert.Equal(new long[] { 3, 1, 5, 2, 6, 4 }, items.Select(r => r.ItemId).ToArray()); // ties by artist, album, title
		// without the flag the order stays alphabetical
		Assert.Equal(1, s.Query(new ReviewQuery { PlayCounts = plays }).Items[0].ItemId);
	}

	[Fact]
	public void AlbumsAndArtistsAddUpTheirPlaysAndCanBeOrderedByThem()
	{
		EvidenceStore s = Library();
		Dictionary<long, int> plays = new Dictionary<long, int> { [1] = 1, [2] = 1, [3] = 5, [4] = 20 };
		ReviewQuery q = new ReviewQuery { SortByPlays = true, PlayCounts = plays };
		AlbumGroupResult albums = s.Albums(q);
		Assert.Equal("Zeta", albums.Items[0].Album);
		Assert.Equal(20, albums.Items[0].Plays);
		Assert.Equal(new[] { "Zeta", "Beta", "Alpha" }, albums.Items.Take(3).Select(a => a.Album).ToArray());
		ArtistGroupResult artists = s.Artists(q);
		Assert.Equal("Zed", artists.Items[0].Artist);
		Assert.Equal(20, artists.Items[0].Plays);
		Assert.Equal(7, artists.Items[1].Plays); // Abe: 1 + 1 + 5
	}

	[Fact]
	public void AWholeArtistCanBeMarkedThroughItsMatchingIds()
	{
		EvidenceStore s = Library();
		IReadOnlyList<long> ids = s.MatchingIds(new ReviewQuery { Artist = "abe" }, 100, out _);
		Assert.Equal(3, ids.Count);
		Assert.Equal(3, s.SetOverride(ids, ManualOverride.Clean, Balanced));
		Assert.Equal(Decision.Clean, s.Get(2)!.Decision);
		Assert.NotEqual(Decision.Clean, s.Get(4)!.Decision);
	}
}
