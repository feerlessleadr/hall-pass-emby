using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;

namespace HallPass.Services;

/// <summary>
/// How often each track has been played, added up over every Emby user (so a track the kids play a lot comes first when reviewing "most played first").
/// Read from Emby's own play counts; nothing is stored. Answers are kept for a minute so paging through a list does not ask again.
/// </summary>
internal static class PlayCounts
{
	private static readonly TimeSpan Keep = TimeSpan.FromSeconds(60);

	private static readonly ConcurrentDictionary<long, (int Plays, DateTime At)> Cache = new ConcurrentDictionary<long, (int, DateTime)>();

	public static IReadOnlyDictionary<long, int> For(IEnumerable<long> ids)
	{
		DateTime now = DateTime.UtcNow;
		if (Cache.Count > 200000)
		{
			Cache.Clear();
		}
		User[] users = Plugin.Instance!.UserManager.Users;
		Dictionary<long, int> result = new Dictionary<long, int>();
		foreach (long id in ids)
		{
			if (Cache.TryGetValue(id, out (int Plays, DateTime At) hit) && now - hit.At < Keep)
			{
				result[id] = hit.Plays;
				continue;
			}
			int total = 0;
			BaseItem? item = Plugin.Instance.LibraryManager.GetItemById(id);
			if (item != null)
			{
				foreach (User user in users)
				{
					try
					{
						total += Math.Max(0, Plugin.Instance.UserDataManager.GetUserData(user, item)?.PlayCount ?? 0);
					}
					catch (Exception ex)
					{
						Plugin.Log("Could not read a play count: " + ex.GetType().Name);
					}
				}
			}
			Cache[id] = (total, now);
			result[id] = total;
		}
		return result;
	}
}
