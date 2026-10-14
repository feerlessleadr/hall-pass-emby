using System.Collections.Generic;

namespace HallPass.Core;

/// <summary>
/// The list of events with their titles, parameters, suggested rules and lock-screen texts, so the phone app can build its rule screen from the
/// server (docs/NOTIFICATIONS.md). Plain dictionaries and lists, so any JSON writer produces the same shape.
/// </summary>
public static class NotificationCatalog
{
	public static Dictionary<string, object?> Build()
	{
		return new Dictionary<string, object?>
		{
			["ApiLevel"] = EventNames.ApiLevel,
			["Service"] = new Dictionary<string, object?>
			{
				["Id"] = "HallPass",
				["Title"] = "Hall Pass",
				["ChannelId"] = "hall-pass",
				["ChannelTitle"] = "Explicit music (Hall Pass)",
			},
			["Events"] = new List<object>
			{
				Entry(EventNames.ScanFinished, "A scan finished",
					"Sent when a scan you started finishes, or when an automatic scan looked at 25 or more new or changed tracks. A few new tracks looked at quietly send nothing.",
					new List<object>(), true, new Dictionary<string, object?>(),
					"Hall Pass finished a scan.", "HallPass/Dashboard"),
				Entry(EventNames.ScanFailed, "A scan did not finish",
					"Sent when a scan failed or was cancelled before it was done.",
					new List<object>(), true, new Dictionary<string, object?>(),
					"A Hall Pass scan did not finish. Unlock for details.", "HallPass/Dashboard"),
				Entry(EventNames.ApplyFinished, "Tags were written or put back",
					"Sent when writing tags (Apply) or putting them back (Undo) finishes. Automatic writing after a scan sends this only when it changed 25 or more tracks.",
					new List<object>(), true, new Dictionary<string, object?>(),
					"Hall Pass finished changing tags.", "HallPass/History"),
				Entry(EventNames.ApplyFailed, "Writing tags did not finish",
					"Sent when writing or putting back tags failed or was cancelled, and when a job was found unfinished after the server stopped in the middle of it.",
					new List<object>(), true, new Dictionary<string, object?>(),
					"Hall Pass could not finish changing tags. Unlock for details.", "HallPass/History"),
				Entry(EventNames.ReviewQueue, "More tracks are waiting for your review",
					"Sent when the number of tracks waiting for you to decide goes up, at most once every 10 minutes.",
					new List<object> { IntParam("MinCount", "When this many tracks are waiting", 1, 100000, 50) },
					false, new Dictionary<string, object?> { ["MinCount"] = 50 },
					"Music is waiting for your review.", "HallPass/Review"),
			},
		};
	}

	private static Dictionary<string, object?> IntParam(string name, string label, int min, int max, int @default) => new Dictionary<string, object?>
	{
		["Name"] = name,
		["Label"] = label,
		["Type"] = "int",
		["Min"] = min,
		["Max"] = max,
		["Default"] = @default,
	};

	private static Dictionary<string, object?> Entry(string shortName, string title, string description, List<object> parameters, bool enabled,
		Dictionary<string, object?> defaultParams, string lockScreenText, string opensTab) => new Dictionary<string, object?>
	{
		["Id"] = EventNames.Full(shortName),
		["Title"] = title,
		["Description"] = description,
		["Params"] = parameters,
		["DefaultRule"] = new Dictionary<string, object?> { ["Enabled"] = enabled, ["Params"] = defaultParams },
		["LockScreenText"] = lockScreenText,
		["OpensTab"] = opensTab,
	};
}
