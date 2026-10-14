using System;
using System.Collections.Generic;
using System.Text.Json;

namespace HallPass.Core;

/// <summary>The message names the phone app listens for (docs/NOTIFICATIONS.md).</summary>
public static class EventNames
{
	public const string Prefix = "HallPass.";

	public const string ScanFinished = "ScanFinished";

	public const string ScanFailed = "ScanFailed";

	public const string ApplyFinished = "ApplyFinished";

	public const string ApplyFailed = "ApplyFailed";

	public const string ReviewQueue = "ReviewQueue";

	/// <summary>The level of the notification contract; grows only when a field is removed or changes meaning. (The plugin's own API version stays 1.)</summary>
	public const int ApiLevel = 1;

	public static string Full(string shortName) => Prefix + shortName;
}

/// <summary>The "Send phone alerts" switches on the plugin page: a master switch and one per event. An event that is off is neither sent nor logged.</summary>
public sealed class NotificationSettings
{
	public bool SendPhoneAlerts { get; set; } = true;

	public bool ScanFinished { get; set; } = true;

	public bool ScanFailed { get; set; } = true;

	public bool ApplyFinished { get; set; } = true;

	public bool ApplyFailed { get; set; } = true;

	public bool ReviewQueue { get; set; }

	public bool IsOn(string shortName)
	{
		if (!SendPhoneAlerts)
		{
			return false;
		}
		switch (shortName)
		{
		case EventNames.ScanFinished: return ScanFinished;
		case EventNames.ScanFailed: return ScanFailed;
		case EventNames.ApplyFinished: return ApplyFinished;
		case EventNames.ApplyFailed: return ApplyFailed;
		case EventNames.ReviewQueue: return ReviewQueue;
		default: return false;
		}
	}
}

/// <summary>An event the aggregator decided to send, before it has a sequence number.</summary>
public sealed class PendingEvent
{
	public PendingEvent(string shortName, Dictionary<string, object?> fields)
	{
		Event = shortName;
		Fields = fields;
	}

	public string Event { get; }

	public Dictionary<string, object?> Fields { get; }
}

/// <summary>An event as sent and logged: the full message name, the time and the whole payload (common fields included).</summary>
public sealed class NotificationEvent
{
	public long Seq { get; set; }

	/// <summary>The message name, for example HallPass.ScanFinished.</summary>
	public string Name { get; set; } = string.Empty;

	public DateTime AtUtc { get; set; }

	public JsonElement Data { get; set; }
}

/// <summary>Turns parsed JSON into plain dictionaries, lists, strings, numbers and booleans, which Emby's own serializer handles everywhere.</summary>
public static class PlainJson
{
	public static object? ToPlain(JsonElement element)
	{
		switch (element.ValueKind)
		{
		case JsonValueKind.Object:
			Dictionary<string, object?> dictionary = new Dictionary<string, object?>();
			foreach (JsonProperty property in element.EnumerateObject())
			{
				dictionary[property.Name] = ToPlain(property.Value);
			}
			return dictionary;
		case JsonValueKind.Array:
			List<object?> list = new List<object?>();
			foreach (JsonElement item in element.EnumerateArray())
			{
				list.Add(ToPlain(item));
			}
			return list;
		case JsonValueKind.String:
			return element.GetString();
		case JsonValueKind.Number:
			return element.TryGetInt64(out long whole) ? (object)whole : element.GetDouble();
		case JsonValueKind.True:
			return true;
		case JsonValueKind.False:
			return false;
		default:
			return null;
		}
	}
}
