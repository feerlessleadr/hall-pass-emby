using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace HallPass.Services;

/// <summary>One album or artist whose tags would change.</summary>
internal sealed record ContainerPlan(BaseItem Item, string Kind, TagChange Change);

/// <summary>
/// The optional roll-up: an album (and/or an artist) gets the explicit tag when any of its tracks is explicit, and loses it when none is. Off by default.
/// Works out which albums and artists to change; the write job does the writing and the journalling like it does for tracks.
/// </summary>
internal static class RollUp
{
	public static bool Enabled(PluginConfiguration c) => c.RollUpAlbums || c.RollUpArtists;

	/// <summary>
	/// The albums and artists to change. <paramref name="scope"/> are the tracks being written; <paramref name="complete"/> says the scope is every decided track, so a
	/// container with no explicit track left can lose its tag (a partial scope, like new tracks, only ever adds the tag).
	/// </summary>
	public static List<ContainerPlan> Plan(PluginConfiguration config, IReadOnlyCollection<TrackRecord> scope, bool complete)
	{
		List<ContainerPlan> plans = new List<ContainerPlan>();
		if (!Enabled(config))
		{
			return plans;
		}
		TagNames names = WriteJob.NamesFor(config);
		HashSet<long> explicitContainers = new HashSet<long>();
		Dictionary<long, string> candidates = new Dictionary<long, string>();

		IEnumerable<TrackRecord> explicitSource = complete ? HallPassState.Store.All().Where((TrackRecord r) => r.Decision == Decision.Explicit) : scope.Where((TrackRecord r) => r.Decision == Decision.Explicit);
		foreach (TrackRecord record in explicitSource)
		{
			foreach ((long id, string kind) in ContainersOf(record.ItemId, config))
			{
				explicitContainers.Add(id);
				candidates[id] = kind;
			}
		}
		if (complete)
		{
			foreach (TrackRecord record in scope)
			{
				foreach ((long id, string kind) in ContainersOf(record.ItemId, config))
				{
					candidates[id] = kind;
				}
			}
		}
		foreach (KeyValuePair<long, string> candidate in candidates)
		{
			BaseItem? item = Plugin.Instance!.LibraryManager.GetItemById(candidate.Key);
			if (item == null)
			{
				continue;
			}
			TagChange change = TagPlan.ForRollUp(item.Tags, names, explicitContainers.Contains(candidate.Key));
			if (change.Changed)
			{
				plans.Add(new ContainerPlan(item, candidate.Value, change));
			}
		}
		return plans;
	}

	/// <summary>The album (its parent) and the artists of a track, as far as the settings ask for them.</summary>
	private static IEnumerable<(long Id, string Kind)> ContainersOf(long trackId, PluginConfiguration config)
	{
		if (Plugin.Instance!.LibraryManager.GetItemById(trackId) is not Audio track)
		{
			yield break;
		}
		if (config.RollUpAlbums && track.AlbumId != 0)
		{
			yield return (track.AlbumId, "album");
		}
		if (config.RollUpArtists)
		{
			if (track.ArtistItems != null)
			{
				foreach (var link in track.ArtistItems)
				{
					yield return (link.Id, "artist");
				}
			}
		}
	}
}
