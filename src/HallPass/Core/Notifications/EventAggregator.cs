using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HallPass.Core;

/// <summary>Who started a scan: the owner (a button or the app), Emby's scheduled task, or the automatic scan of new tracks.</summary>
public enum RunOrigin
{
	Manual,
	Scheduled,
	Auto,
}

/// <summary>What a scan ended with.</summary>
public sealed class ScanOutcome
{
	/// <summary>Completed, Failed or Cancelled.</summary>
	public string State { get; set; } = string.Empty;

	public RunOrigin Origin { get; set; }

	public int Total { get; set; }

	public int Scanned { get; set; }

	public int Unchanged { get; set; }

	public int Failed { get; set; }

	public int OnlineFailed { get; set; }

	/// <summary>Tracks waiting for a person now.</summary>
	public int NeedsReview { get; set; }

	/// <summary>The sentence the scan ended with (used for a failure).</summary>
	public string Message { get; set; } = string.Empty;
}

/// <summary>What a write job (apply or undo) ended with.</summary>
public sealed class WriteOutcome
{
	/// <summary>Completed, Failed or Cancelled.</summary>
	public string State { get; set; } = string.Empty;

	/// <summary>"Apply" or "Undo".</summary>
	public string Kind { get; set; } = "Apply";

	/// <summary>True for writing that nobody asked for just now (after a scan, or the scheduled run).</summary>
	public bool Automatic { get; set; }

	public int Changed { get; set; }

	public int Unchanged { get; set; }

	public int Failed { get; set; }

	public int Conflicts { get; set; }

	public string Message { get; set; } = string.Empty;
}

/// <summary>A journal file with no end line.</summary>
public sealed class UnfinishedJob
{
	public string JobId { get; set; } = string.Empty;

	/// <summary>The journal's own kind: apply, auto or undo.</summary>
	public string Kind { get; set; } = string.Empty;

	public DateTime StartedUtc { get; set; }
}

/// <summary>What the aggregator remembers between runs (saved, so a restart does not repeat alerts).</summary>
public sealed class NotificationState
{
	/// <summary>The number of tracks waiting for a person the last time it was looked at; -1 until the first look.</summary>
	public int ReviewLastCount { get; set; } = -1;

	public DateTime ReviewLastSentUtc { get; set; } = DateTime.MinValue;

	/// <summary>Journal jobs found unfinished and already announced (an interrupted job is announced once).</summary>
	public List<string> AnnouncedUnfinished { get; set; } = new List<string>();
}

/// <summary>
/// Turns what happened (a scan ended, a write job ended, the review queue changed) into the few events worth a phone alert
/// (docs/NOTIFICATIONS.md): one summary per scan or run, never one per track; a quiet automatic scan sends nothing; the review queue is announced
/// only when it grew and at most once in ten minutes; an interrupted job is announced once. No event carries a file path. A switch that is off keeps
/// the bookkeeping (so switching it on later does not flood) but sends nothing and logs nothing. Safe to call from several threads.
/// </summary>
public sealed class EventAggregator
{
	/// <summary>An automatic scan or automatic write has to change at least this many tracks to be worth an alert.</summary>
	public const int AutoQuietLimit = 25;

	public static readonly TimeSpan ReviewQueueSpacing = TimeSpan.FromMinutes(10);

	public const int MaxMessageLength = 300;

	// Folder names may contain spaces, so every folder segment is taken whole; the last segment (a file name) stops at the first space.
	private static readonly Regex DrivePath = new Regex(@"[A-Za-z]:[\\/](?:[^\\/\r\n'"",;)]+[\\/])*[^\\/\r\n'"",;)\s]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex QuotedDrivePath = new Regex(@"(?<q>['""])[A-Za-z]:[\\/][^\r\n'""]*\k<q>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex SharePath = new Regex(@"\\\\[^\s,;)'""]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex UnixPath = new Regex(@"(?<![\w:/.])/(?:[^/\r\n'"",;)]+/)+[^\s/'"",;)]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private readonly NotificationState _state;

	private readonly object _gate = new object();

	private int _autoTotal;

	private int _autoScanned;

	private int _autoUnchanged;

	private int _autoFailed;

	private int _autoOnlineFailed;

	public EventAggregator(NotificationState state)
	{
		_state = state;
	}

	public NotificationState State => _state;

	/// <summary>A scan that ran as one job (started by the owner or by the scheduled task) ended.</summary>
	public List<PendingEvent> ScanEnded(ScanOutcome o, NotificationSettings settings)
	{
		lock (_gate)
		{
			List<PendingEvent> events = new List<PendingEvent>();
			if (o.State == "Completed")
			{
				bool worthIt = o.Origin == RunOrigin.Manual || o.Scanned >= AutoQuietLimit;
				if (worthIt && settings.IsOn(EventNames.ScanFinished))
				{
					events.Add(ScanFinishedEvent(o.Total, o.Scanned, o.Unchanged, o.Failed, o.NeedsReview, o.OnlineFailed));
				}
			}
			else if ((o.State == "Failed" || o.State == "Cancelled") && settings.IsOn(EventNames.ScanFailed))
			{
				string message = o.State == "Cancelled"
					? "The scan was cancelled before it finished. What was scanned is kept."
					: Clip(StripPaths(o.Message.Length > 0 ? o.Message : "The scan failed."));
				events.Add(new PendingEvent(EventNames.ScanFailed, new Dictionary<string, object?> { ["State"] = o.State, ["Message"] = message }));
			}
			return events;
		}
	}

	/// <summary>One batch of the automatic scan of new tracks is done. Nothing is sent until <see cref="AutoFlush"/>.</summary>
	public void AutoBatch(int total, int scanned, int unchanged, int failed, int onlineFailed)
	{
		lock (_gate)
		{
			_autoTotal += total;
			_autoScanned += scanned;
			_autoUnchanged += unchanged;
			_autoFailed += failed;
			_autoOnlineFailed += Math.Max(0, onlineFailed);
		}
	}

	/// <summary>The automatic scan has nothing left to do: one summary for everything it looked at, but only if it was 25 or more tracks.</summary>
	public List<PendingEvent> AutoFlush(int needsReview, NotificationSettings settings)
	{
		lock (_gate)
		{
			List<PendingEvent> events = new List<PendingEvent>();
			int total = _autoTotal, scanned = _autoScanned, unchanged = _autoUnchanged, failed = _autoFailed, online = _autoOnlineFailed;
			_autoTotal = _autoScanned = _autoUnchanged = _autoFailed = _autoOnlineFailed = 0;
			if (scanned >= AutoQuietLimit && settings.IsOn(EventNames.ScanFinished))
			{
				events.Add(ScanFinishedEvent(total, scanned, unchanged, failed, needsReview, online));
			}
			return events;
		}
	}

	/// <summary>True while the automatic scan has looked at tracks that have not been summed up yet.</summary>
	public bool AutoPending
	{
		get
		{
			lock (_gate)
			{
				return _autoTotal > 0;
			}
		}
	}

	private static PendingEvent ScanFinishedEvent(int total, int scanned, int unchanged, int failed, int needsReview, int onlineFailed) => new PendingEvent(
		EventNames.ScanFinished,
		new Dictionary<string, object?>
		{
			["Total"] = total,
			["Scanned"] = scanned,
			["Unchanged"] = unchanged,
			["Failed"] = failed,
			["NeedsReview"] = needsReview,
			["OnlineFailed"] = onlineFailed,
		});

	/// <summary>A write job ended.</summary>
	public List<PendingEvent> WriteEnded(WriteOutcome o, NotificationSettings settings)
	{
		lock (_gate)
		{
			List<PendingEvent> events = new List<PendingEvent>();
			string kind = o.Kind == "Undo" ? "Undo" : "Apply";
			if (o.State == "Completed")
			{
				bool worthIt = !o.Automatic || o.Changed >= AutoQuietLimit;
				if (worthIt && settings.IsOn(EventNames.ApplyFinished))
				{
					events.Add(new PendingEvent(EventNames.ApplyFinished, new Dictionary<string, object?>
					{
						["Kind"] = kind,
						["Changed"] = o.Changed,
						["Unchanged"] = o.Unchanged,
						["Failed"] = o.Failed,
						["Conflicts"] = o.Conflicts,
					}));
				}
			}
			else if ((o.State == "Failed" || o.State == "Cancelled") && settings.IsOn(EventNames.ApplyFailed))
			{
				string noun = kind == "Undo" ? "undo" : "apply";
				string message = o.State == "Cancelled"
					? "The " + noun + " was cancelled before it finished. " + (kind == "Undo" ? "Tracks already put back stay as they are." : "Tags already written stay and can be undone from History.")
					: Clip(StripPaths(o.Message.Length > 0 ? o.Message : "The " + noun + " failed."));
				events.Add(new PendingEvent(EventNames.ApplyFailed, new Dictionary<string, object?> { ["Kind"] = kind, ["State"] = o.State, ["Message"] = message }));
			}
			return events;
		}
	}

	/// <summary>
	/// Journal files with no end line were found when the plugin started: the server stopped in the middle of a job. Each such job is announced
	/// once (several make one event about the newest). Returns the events; the jobs are remembered even when the switch is off.
	/// </summary>
	public List<PendingEvent> UnfinishedFound(IReadOnlyList<UnfinishedJob> jobs, NotificationSettings settings)
	{
		lock (_gate)
		{
			List<PendingEvent> events = new List<PendingEvent>();
			_state.AnnouncedUnfinished.RemoveAll((string id) => jobs.All((UnfinishedJob j) => j.JobId != id));
			List<UnfinishedJob> fresh = jobs.Where((UnfinishedJob j) => !_state.AnnouncedUnfinished.Contains(j.JobId)).OrderByDescending((UnfinishedJob j) => j.StartedUtc).ToList();
			if (fresh.Count == 0)
			{
				return events;
			}
			foreach (UnfinishedJob job in fresh)
			{
				_state.AnnouncedUnfinished.Add(job.JobId);
			}
			if (settings.IsOn(EventNames.ApplyFailed))
			{
				UnfinishedJob newest = fresh[0];
				string kind = newest.Kind == "undo" ? "Undo" : "Apply";
				string noun = kind == "Undo" ? "undo" : "apply";
				string message = "An " + noun + " that started on " + newest.StartedUtc.ToString("yyyy-MM-dd 'at' HH:mm") + " UTC did not finish, probably because the server stopped. "
					+ "Tags it already wrote stay as they are; open History to see what it did and to undo it.";
				if (fresh.Count > 1)
				{
					message += " " + (fresh.Count - 1) + " older unfinished " + (fresh.Count == 2 ? "job was" : "jobs were") + " found as well.";
				}
				events.Add(new PendingEvent(EventNames.ApplyFailed, new Dictionary<string, object?> { ["Kind"] = kind, ["State"] = "Interrupted", ["Message"] = message }));
			}
			return events;
		}
	}

	/// <summary>
	/// The number of tracks waiting for a person is now <paramref name="count"/>. An event goes out only when it went up since the last look and at
	/// least ten minutes passed since the last one. A rise held back by the spacing is announced by a later look. With <paramref name="silent"/> the
	/// number is only remembered (after something the owner did, or at startup).
	/// </summary>
	public List<PendingEvent> ReviewQueueChanged(int count, DateTime nowUtc, NotificationSettings settings, bool silent)
	{
		lock (_gate)
		{
			List<PendingEvent> events = new List<PendingEvent>();
			int last = _state.ReviewLastCount;
			if (last < 0 || silent || count < last)
			{
				_state.ReviewLastCount = count;
				return events;
			}
			if (count == last)
			{
				return events;
			}
			if (nowUtc - _state.ReviewLastSentUtc < ReviewQueueSpacing)
			{
				return events;
			}
			_state.ReviewLastCount = count;
			_state.ReviewLastSentUtc = nowUtc;
			if (settings.IsOn(EventNames.ReviewQueue))
			{
				events.Add(new PendingEvent(EventNames.ReviewQueue, new Dictionary<string, object?> { ["Count"] = count, ["New"] = count - last }));
			}
			return events;
		}
	}

	/// <summary>Takes any drive path, network path or Unix path out of a sentence. Events never carry them.</summary>
	public static string StripPaths(string? text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}
		string result = QuotedDrivePath.Replace(text, "(a folder)");
		result = DrivePath.Replace(result, "(a folder)");
		result = SharePath.Replace(result, "(a folder)");
		result = UnixPath.Replace(result, "(a folder)");
		return result;
	}

	private static string Clip(string text) => text.Length <= MaxMessageLength ? text : text.Substring(0, MaxMessageLength - 3) + "...";
}
