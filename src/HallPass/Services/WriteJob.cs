using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace HallPass.Services;

public sealed class WriteStatus
{
	/// <summary>Idle, Running, Completed, Cancelled or Failed.</summary>
	public string State { get; init; } = "Idle";

	/// <summary>"apply" or "undo".</summary>
	public string Kind { get; init; } = string.Empty;

	public string JobId { get; init; } = string.Empty;

	public int Total { get; init; }

	public int Processed { get; init; }

	public int Changed { get; init; }

	public int Unchanged { get; init; }

	public int Failed { get; init; }

	/// <summary>Undo only: tracks left alone because their tags were changed by someone since.</summary>
	public int Conflicts { get; init; }

	public DateTime? StartedUtc { get; init; }

	public DateTime? FinishedUtc { get; init; }

	public string Message { get; init; } = string.Empty;
}

/// <summary>Writing tags (apply) and putting them back (undo): background jobs with progress, cancel and a journal. One at a time, never together with a scan.</summary>
internal static class WriteJob
{
	private static readonly object Gate = new object();

	private static WriteStatus _status = new WriteStatus();

	private static CancellationTokenSource? _cts;

	public static WriteStatus Status
	{
		get
		{
			lock (Gate)
			{
				return _status;
			}
		}
	}

	public static TagNames NamesFor(PluginConfiguration c) => new TagNames(c.ExplicitTag, c.CleanTag, c.LikelyCleanTag, c.ReviewTag);

	public static WriteStatus StartApply()
	{
		if (HallPassState.Config.DryRun)
		{
			throw new InvalidOperationException("Dry run is on, so no tag is written. Switch it off in Settings first.");
		}
		return Start("apply", null, false, null);
	}

	/// <summary>Writes the tags of just these tracks (after the automatic scan of new tracks): the same rules as an apply, journalled as "auto".</summary>
	public static WriteStatus StartAuto(IReadOnlyCollection<long> onlyIds)
	{
		if (HallPassState.Config.DryRun)
		{
			throw new InvalidOperationException("Dry run is on, so no tag is written.");
		}
		return Start("auto", null, false, onlyIds);
	}

	public static WriteStatus StartUndo(string jobId, bool force)
	{
		JobSummary? original = JournalStore.Summary(jobId);
		if (original == null)
		{
			throw new InvalidOperationException("That job is not in the journal (it may have been tidied away).");
		}
		return Start("undo", jobId, force, null);
	}

	private static WriteStatus Start(string kind, string? undoOf, bool force, IReadOnlyCollection<long>? onlyIds)
	{
		lock (Gate)
		{
			if (!JobGate.TryEnter(kind))
			{
				throw new InvalidOperationException("A " + JobGate.Running + " job is already running.");
			}
			_cts = new CancellationTokenSource();
			string jobId = JournalStore.NewJobId();
			_status = new WriteStatus { State = "Running", Kind = kind, JobId = jobId, StartedUtc = DateTime.UtcNow, Message = "Starting" };
			CancellationToken token = _cts.Token;
			Task.Run(() => RunAsync(kind, jobId, undoOf, force, onlyIds, token));
			return _status;
		}
	}

	public static void Cancel()
	{
		lock (Gate)
		{
			_cts?.Cancel();
		}
	}

	private static void Update(Func<WriteStatus, WriteStatus> change)
	{
		lock (Gate)
		{
			_status = change(_status);
		}
	}

	private static async Task RunAsync(string kind, string jobId, string? undoOf, bool force, IReadOnlyCollection<long>? onlyIds, CancellationToken token)
	{
		int total = 0, processed = 0, changed = 0, unchanged = 0, failed = 0, conflicts = 0;
		bool cancelled = false;
		JournalWriter? journal = null;
		List<(BaseItem Item, JournalEntry Entry)> batch = new List<(BaseItem, JournalEntry)>();
		try
		{
			PluginConfiguration config = HallPassState.Config;
			int batchSize = Math.Clamp(config.BatchSize, 1, 2000);
			journal = JournalStore.Begin(jobId, kind, undoOf);

			IEnumerable<Func<(BaseItem Item, JournalEntry? Entry, bool Skip, bool Missing, bool Conflict)>> work = kind != "undo"
				? PlanApply(config, onlyIds, out total)
				: PlanUndo(undoOf!, force, out total);
			Update((WriteStatus s) => Copy(s, total: total, message: (kind != "undo" ? "Writing tags to " : "Putting back ") + total + " tracks"));

			foreach (Func<(BaseItem Item, JournalEntry? Entry, bool Skip, bool Missing, bool Conflict)> step in work)
			{
				token.ThrowIfCancellationRequested();
				(BaseItem Item, JournalEntry? Entry, bool Skip, bool Missing, bool Conflict) r = step();
				processed++;
				if (r.Missing)
				{
					failed++;
				}
				else if (r.Conflict)
				{
					conflicts++;
				}
				else if (r.Skip || r.Entry == null)
				{
					unchanged++;
				}
				else
				{
					batch.Add((r.Item, r.Entry));
				}
				if (batch.Count >= batchSize)
				{
					(int ok, int bad) = await FlushAsync(batch, journal, config, token).ConfigureAwait(false);
					changed += ok;
					failed += bad;
					batch.Clear();
					int p = processed, c = changed, u = unchanged, f = failed, cf = conflicts;
					Update((WriteStatus s) => Copy(s, processed: p, changed: c, unchanged: u, failed: f, conflicts: cf));
					await Task.Delay(Math.Max(0, config.BatchDelayMs), token).ConfigureAwait(false);
				}
			}
			if (batch.Count > 0)
			{
				(int ok, int bad) = await FlushAsync(batch, journal, config, token).ConfigureAwait(false);
				changed += ok;
				failed += bad;
				batch.Clear();
			}
		}
		catch (OperationCanceledException)
		{
			cancelled = true;
			// tracks changed in memory but not saved yet: put them back so Emby's cached items match its database
			foreach ((BaseItem item, JournalEntry entry) in batch)
			{
				RevertInMemory(item, entry);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log("The " + kind + " job failed: " + ex.GetType().Name + " " + ex.Message);
			journal?.Finish(new JournalTrailer { FinishedUtc = DateTime.UtcNow, Changed = changed, Unchanged = unchanged, Failed = failed, Conflicts = conflicts });
			journal?.Dispose();
			JobGate.Exit();
			Update((WriteStatus s) => Copy(s, state: "Failed", finished: true, message: "The " + kind + " failed: " + ex.Message));
			return;
		}

		journal?.Finish(new JournalTrailer { FinishedUtc = DateTime.UtcNow, Changed = changed, Unchanged = unchanged, Failed = failed, Conflicts = conflicts, Cancelled = cancelled });
		journal?.Dispose();
		if (kind == "undo" && undoOf != null && !cancelled)
		{
			JournalStore.MarkUndone(undoOf, jobId);
		}
		JournalStore.Prune(HallPassState.Config.JournalKeepJobs, HallPassState.Config.JournalKeepDays);
		if (kind == "apply")
		{
			// the tags changed in the library: nothing in the store changes, but the dashboard's preview is now out of date
		}
		JobGate.Exit();
		int pf = processed, cf2 = changed, uf = unchanged, ff = failed, cc = conflicts;
		string verb = kind != "undo" ? "written" : "put back";
		Update((WriteStatus s) => Copy(s, state: cancelled ? "Cancelled" : "Completed", processed: pf, changed: cf2, unchanged: uf, failed: ff, conflicts: cc, finished: true,
			message: (cancelled ? "Cancelled: " : "Done: ") + cf2 + " " + verb + ", " + uf + " already right" + (ff > 0 ? ", " + ff + " failed" : string.Empty) + (cc > 0 ? ", " + cc + " left alone (edited since)" : string.Empty)));
	}

	/// <summary>Changes the tags of a batch in Emby (journal first, so an interruption can be undone) and says how many were saved.</summary>
	private static async Task<(int Ok, int Bad)> FlushAsync(List<(BaseItem Item, JournalEntry Entry)> batch, JournalWriter journal, PluginConfiguration config, CancellationToken token)
	{
		foreach ((BaseItem _, JournalEntry entry) in batch)
		{
			journal.Append(JournalFormat.ToLine(entry));
		}
		journal.Flush();
		try
		{
			Plugin.Instance!.LibraryManager.UpdateItems(batch.Select(((BaseItem Item, JournalEntry Entry) b) => b.Item).ToList(), null, ItemUpdateType.MetadataEdit, true, config.RunMetadataSavers, null, token);
			await Task.CompletedTask.ConfigureAwait(false);
			return (batch.Count, 0);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			foreach ((BaseItem item, JournalEntry entry) in batch)
			{
				RevertInMemory(item, entry);
			}
			Plugin.Log("Could not save a batch of " + batch.Count + " tracks: " + ex.GetType().Name + " " + ex.Message);
			return (0, batch.Count);
		}
	}

	private static void RevertInMemory(BaseItem item, JournalEntry entry)
	{
		item.SetTags(entry.Before);
		if (entry.LockAdded)
		{
			item.LockedFields = item.LockedFields.Where((MetadataFields f) => f != MetadataFields.Tags).ToArray();
		}
	}

	// ----- planning: one step per track, run lazily so the first batch starts at once ------------------------------

	private static IEnumerable<Func<(BaseItem Item, JournalEntry? Entry, bool Skip, bool Missing, bool Conflict)>> PlanApply(PluginConfiguration config, IReadOnlyCollection<long>? onlyIds, out int total)
	{
		TagNames names = NamesFor(config);
		List<TrackRecord> records = HallPassState.Store.All().Where((TrackRecord r) => r.Decision != Decision.Unscanned && (onlyIds == null || onlyIds.Contains(r.ItemId))).ToList();
		List<ContainerPlan> containers = RollUp.Plan(config, records, onlyIds == null);
		total = records.Count + containers.Count;
		bool skippedAsClean = config.TagSkippedAsClean;
		IEnumerable<Func<(BaseItem, JournalEntry?, bool, bool, bool)>> containerSteps = containers.Select((ContainerPlan plan) => (Func<(BaseItem, JournalEntry?, bool, bool, bool)>)(() =>
		{
			BaseItem item = plan.Item;
			bool lockAdded = !item.LockedFields.Contains(MetadataFields.Tags);
			item.SetTags(plan.Change.After);
			if (lockAdded)
			{
				item.LockedFields = item.LockedFields.Concat(new[] { MetadataFields.Tags }).ToArray();
			}
			return (item, new JournalEntry { ItemId = item.InternalId, Name = (plan.Kind == "album" ? "Album: " : "Artist: ") + item.Name, Before = plan.Change.Before, After = plan.Change.After, LockAdded = lockAdded }, false, false, false);
		})).ToList();
		return records.Select((TrackRecord record) => (Func<(BaseItem, JournalEntry?, bool, bool, bool)>)(() =>
		{
			BaseItem? item = Plugin.Instance!.LibraryManager.GetItemById(record.ItemId);
			if (item == null)
			{
				return (null!, null, false, true, false);
			}
			TagChange plan = TagPlan.ForDecision(item.Tags, names, record.Decision, skippedAsClean);
			if (!plan.Changed)
			{
				return (item, null, true, false, false);
			}
			bool lockAdded = !item.LockedFields.Contains(MetadataFields.Tags);
			item.SetTags(plan.After);
			if (lockAdded)
			{
				item.LockedFields = item.LockedFields.Concat(new[] { MetadataFields.Tags }).ToArray();
			}
			return (item, new JournalEntry { ItemId = record.ItemId, Name = record.Name, Before = plan.Before, After = plan.After, LockAdded = lockAdded }, false, false, false);
		})).Concat(containerSteps).ToList();
	}

	private static IEnumerable<Func<(BaseItem Item, JournalEntry? Entry, bool Skip, bool Missing, bool Conflict)>> PlanUndo(string undoOf, bool force, out int total)
	{
		List<JournalEntry> entries = JournalStore.ReadEntries(undoOf).ToList();
		total = entries.Count;
		return entries.Select((JournalEntry original) => (Func<(BaseItem, JournalEntry?, bool, bool, bool)>)(() =>
		{
			BaseItem? item = Plugin.Instance!.LibraryManager.GetItemById(original.ItemId);
			if (item == null)
			{
				return (null!, null, false, true, false);
			}
			string[] current = item.Tags;
			switch (JournalFormat.DecideUndo(current, original, force))
			{
			case UndoAction.AlreadyRestored:
				return (item, null, true, false, false);
			case UndoAction.Conflict:
				return (item, null, false, false, true);
			}
			item.SetTags(original.Before);
			if (original.LockAdded && item.LockedFields.Contains(MetadataFields.Tags))
			{
				item.LockedFields = item.LockedFields.Where((MetadataFields f) => f != MetadataFields.Tags).ToArray();
			}
			return (item, new JournalEntry { ItemId = original.ItemId, Name = original.Name, Before = current, After = original.Before, LockAdded = false }, false, false, false);
		})).ToList();
	}

	private static WriteStatus Copy(WriteStatus s, string? state = null, int? total = null, int? processed = null, int? changed = null, int? unchanged = null, int? failed = null, int? conflicts = null, bool finished = false, string? message = null)
	{
		return new WriteStatus
		{
			State = state ?? s.State,
			Kind = s.Kind,
			JobId = s.JobId,
			Total = total ?? s.Total,
			Processed = processed ?? s.Processed,
			Changed = changed ?? s.Changed,
			Unchanged = unchanged ?? s.Unchanged,
			Failed = failed ?? s.Failed,
			Conflicts = conflicts ?? s.Conflicts,
			StartedUtc = s.StartedUtc,
			FinishedUtc = finished ? DateTime.UtcNow : s.FinishedUtc,
			Message = message ?? s.Message,
		};
	}
}
