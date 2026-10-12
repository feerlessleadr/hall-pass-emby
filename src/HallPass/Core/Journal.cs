using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HallPass.Core;

/// <summary>One track changed by a job: enough to put it back.</summary>
public sealed class JournalEntry
{
	public string Type { get; set; } = "item";

	public long ItemId { get; set; }

	public string Name { get; set; } = string.Empty;

	public string[] Before { get; set; } = Array.Empty<string>();

	public string[] After { get; set; } = Array.Empty<string>();

	/// <summary>True when the job itself locked the Tags field (so an undo unlocks it again).</summary>
	public bool LockAdded { get; set; }
}

public sealed class JournalHeader
{
	public string Type { get; set; } = "job";

	public string JobId { get; set; } = string.Empty;

	/// <summary>"apply" or "undo".</summary>
	public string Kind { get; set; } = "apply";

	public DateTime StartedUtc { get; set; }

	/// <summary>For an undo job: the job it reverses.</summary>
	public string? UndoOf { get; set; }
}

public sealed class JournalTrailer
{
	public string Type { get; set; } = "end";

	public DateTime FinishedUtc { get; set; }

	public int Changed { get; set; }

	public int Unchanged { get; set; }

	public int Failed { get; set; }

	public int Conflicts { get; set; }

	public bool Cancelled { get; set; }
}

public enum UndoAction
{
	/// <summary>Put the earlier tags back.</summary>
	Restore,

	/// <summary>The track already has its earlier tags.</summary>
	AlreadyRestored,

	/// <summary>The track's tags were changed by someone since: left alone unless forced.</summary>
	Conflict,
}

/// <summary>Reading and writing journal lines (one JSON object per line) and the undo rules. Pure logic.</summary>
public static class JournalFormat
{
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

	public static string ToLine(JournalHeader header) => JsonSerializer.Serialize(header, Json);

	public static string ToLine(JournalEntry entry) => JsonSerializer.Serialize(entry, Json);

	public static string ToLine(JournalTrailer trailer) => JsonSerializer.Serialize(trailer, Json);

	public static JournalHeader? ReadHeader(string? line) => Read<JournalHeader>(line, "job");

	public static JournalEntry? ReadEntry(string? line) => Read<JournalEntry>(line, "item");

	public static JournalTrailer? ReadTrailer(string? line) => Read<JournalTrailer>(line, "end");

	private static T? Read<T>(string? line, string type) where T : class
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return null;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(line);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("Type", out JsonElement t) || t.GetString() != type)
			{
				return null;
			}
			return JsonSerializer.Deserialize<T>(line, Json);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>Whether to put an entry's earlier tags back. A track changed since the job (its tags are neither the "after" nor the "before") is a conflict unless forced.</summary>
	public static UndoAction DecideUndo(IReadOnlyList<string> currentTags, JournalEntry entry, bool force)
	{
		if (SameTags(currentTags, entry.Before))
		{
			return UndoAction.AlreadyRestored;
		}
		if (force || SameTags(currentTags, entry.After))
		{
			return UndoAction.Restore;
		}
		return UndoAction.Conflict;
	}

	/// <summary>The same tags, ignoring order and letter case.</summary>
	public static bool SameTags(IEnumerable<string> a, IEnumerable<string> b)
	{
		HashSet<string> x = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
		return x.SetEquals(b);
	}

	/// <summary>Which finished jobs to keep: the newest <paramref name="keepJobs"/>, plus any younger than <paramref name="keepDays"/> days.</summary>
	public static IReadOnlyList<string> JobsToDelete(IReadOnlyList<(string JobId, DateTime StartedUtc)> jobs, int keepJobs, int keepDays, DateTime nowUtc)
	{
		List<(string JobId, DateTime StartedUtc)> newestFirst = jobs.OrderByDescending(((string JobId, DateTime StartedUtc) j) => j.StartedUtc).ToList();
		return newestFirst.Skip(Math.Max(0, keepJobs)).Where(((string JobId, DateTime StartedUtc) j) => j.StartedUtc < nowUtc.AddDays(-Math.Max(0, keepDays))).Select(((string JobId, DateTime StartedUtc) j) => j.JobId).ToList();
	}
}
