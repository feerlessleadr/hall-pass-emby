using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HallPass.Core;

namespace HallPass.Services;

public sealed class JobSummary
{
	public string JobId { get; init; } = string.Empty;

	public string Kind { get; init; } = string.Empty;

	public DateTime StartedUtc { get; init; }

	public DateTime? FinishedUtc { get; init; }

	/// <summary>True when the file has no end line (still running, or Emby stopped in the middle).</summary>
	public bool Unfinished { get; init; }

	public int Changed { get; init; }

	public int Unchanged { get; init; }

	public int Failed { get; init; }

	public int Conflicts { get; init; }

	public bool Cancelled { get; init; }

	/// <summary>For an undo job: the job it reversed.</summary>
	public string? UndoOf { get; init; }

	/// <summary>For an apply job: the undo job that reversed it, if any.</summary>
	public string? UndoneBy { get; init; }
}

/// <summary>The change journal: one JSON-lines file per job (a header line, one line per changed track, an end line), so an apply can be undone.</summary>
internal static class JournalStore
{
	private static readonly object Gate = new object();

	private static string Directory => Path.Combine(Plugin.Instance!.AppPaths.PluginConfigurationsPath, "HallPass.Journal");

	public static string NewJobId() => Guid.NewGuid().ToString("N");

	/// <summary>Job ids are file names: only letters and digits are accepted (no path tricks).</summary>
	public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All((char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));

	private static string PathFor(string jobId) => Path.Combine(Directory, jobId + ".jsonl");

	private static string UndoneMarker(string jobId) => Path.Combine(Directory, jobId + ".undone");

	public static JournalWriter Begin(string jobId, string kind, string? undoOf)
	{
		System.IO.Directory.CreateDirectory(Directory);
		JournalWriter writer = new JournalWriter(PathFor(jobId));
		writer.Append(JournalFormat.ToLine(new JournalHeader { JobId = jobId, Kind = kind, StartedUtc = DateTime.UtcNow, UndoOf = undoOf }));
		return writer;
	}

	public static IEnumerable<JournalEntry> ReadEntries(string jobId)
	{
		if (!IsValidId(jobId) || !File.Exists(PathFor(jobId)))
		{
			yield break;
		}
		foreach (string line in File.ReadLines(PathFor(jobId)))
		{
			JournalEntry? entry = JournalFormat.ReadEntry(line);
			if (entry != null)
			{
				yield return entry;
			}
		}
	}

	public static JobSummary? Summary(string jobId)
	{
		if (!IsValidId(jobId) || !File.Exists(PathFor(jobId)))
		{
			return null;
		}
		try
		{
			string? first = File.ReadLines(PathFor(jobId)).FirstOrDefault();
			JournalHeader? header = JournalFormat.ReadHeader(first);
			if (header == null)
			{
				return null;
			}
			string? last = File.ReadLines(PathFor(jobId)).LastOrDefault((string l) => !string.IsNullOrWhiteSpace(l));
			JournalTrailer? trailer = JournalFormat.ReadTrailer(last);
			string? undoneBy = File.Exists(UndoneMarker(jobId)) ? File.ReadAllText(UndoneMarker(jobId)).Trim() : null;
			return new JobSummary
			{
				JobId = header.JobId,
				Kind = header.Kind,
				StartedUtc = header.StartedUtc,
				FinishedUtc = trailer?.FinishedUtc,
				Unfinished = trailer == null,
				Changed = trailer?.Changed ?? 0,
				Unchanged = trailer?.Unchanged ?? 0,
				Failed = trailer?.Failed ?? 0,
				Conflicts = trailer?.Conflicts ?? 0,
				Cancelled = trailer?.Cancelled ?? false,
				UndoOf = header.UndoOf,
				UndoneBy = string.IsNullOrEmpty(undoneBy) ? null : undoneBy,
			};
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>All journalled jobs, newest first.</summary>
	public static IReadOnlyList<JobSummary> List()
	{
		if (!System.IO.Directory.Exists(Directory))
		{
			return new List<JobSummary>();
		}
		return System.IO.Directory.EnumerateFiles(Directory, "*.jsonl")
			.Select((string f) => Summary(Path.GetFileNameWithoutExtension(f)))
			.Where((JobSummary? s) => s != null)
			.Select((JobSummary? s) => s!)
			.OrderByDescending((JobSummary s) => s.StartedUtc)
			.ToList();
	}

	public static void MarkUndone(string jobId, string undoJobId)
	{
		if (IsValidId(jobId) && IsValidId(undoJobId))
		{
			File.WriteAllText(UndoneMarker(jobId), undoJobId);
		}
	}

	/// <summary>Deletes old finished journals: keeps the newest N and anything younger than the days.</summary>
	public static void Prune(int keepJobs, int keepDays)
	{
		lock (Gate)
		{
			try
			{
				IReadOnlyList<JobSummary> all = List().Where((JobSummary s) => !s.Unfinished).ToList();
				foreach (string id in JournalFormat.JobsToDelete(all.Select((JobSummary s) => (s.JobId, s.StartedUtc)).ToList(), keepJobs, keepDays, DateTime.UtcNow))
				{
					File.Delete(PathFor(id));
					if (File.Exists(UndoneMarker(id)))
					{
						File.Delete(UndoneMarker(id));
					}
				}
			}
			catch (IOException ex)
			{
				Plugin.Log("Could not tidy the journal: " + ex.Message);
			}
		}
	}
}

/// <summary>Appends journal lines and flushes each batch to disk (so a crash loses at most the batch in flight).</summary>
internal sealed class JournalWriter : IDisposable
{
	private readonly StreamWriter _writer;

	public JournalWriter(string path)
	{
		_writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
	}

	public void Append(string line)
	{
		_writer.WriteLine(line);
	}

	public void Flush()
	{
		_writer.Flush();
		((FileStream)_writer.BaseStream).Flush(true);
	}

	public void Finish(JournalTrailer trailer)
	{
		Append(JournalFormat.ToLine(trailer));
		Flush();
	}

	public void Dispose()
	{
		_writer.Dispose();
	}
}
