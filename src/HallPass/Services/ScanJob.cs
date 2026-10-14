using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;
using MediaBrowser.Controller.Entities;

namespace HallPass.Services;

public sealed class ScanStatus
{
	/// <summary>Idle, Running, Completed, Cancelled or Failed.</summary>
	public string State { get; init; } = "Idle";

	public bool Force { get; init; }

	public int Total { get; init; }

	public int Processed { get; init; }

	/// <summary>Tracks that were looked at (new or changed).</summary>
	public int Scanned { get; init; }

	/// <summary>Tracks left as they were (nothing changed since the last scan).</summary>
	public int Unchanged { get; init; }

	public int Failed { get; init; }

	/// <summary>Tracks forgotten because they are no longer in the library.</summary>
	public int Removed { get; init; }

	/// <summary>Online lookups: questions sent to a catalog, answers that came from the saved cache, and questions that got no usable answer.</summary>
	public int OnlineAsked { get; init; }

	public int OnlineCached { get; init; }

	public int OnlineFailed { get; init; }

	public DateTime? StartedUtc { get; init; }

	public DateTime? FinishedUtc { get; init; }

	public string Message { get; init; } = string.Empty;
}

/// <summary>The scan: one at a time, in the background, with progress and cancel. A dry run: it records decisions and writes no tags.</summary>
internal static class ScanJob
{
	private static readonly object Gate = new object();

	private static ScanStatus _status = new ScanStatus();

	private static CancellationTokenSource? _cts;

	public static ScanStatus Status
	{
		get
		{
			lock (Gate)
			{
				return _status;
			}
		}
	}

	public static bool IsRunning => Status.State == "Running";

	public static ScanStatus Start(bool force)
	{
		lock (Gate)
		{
			if (!JobGate.TryEnter("scan"))
			{
				throw new InvalidOperationException("A " + JobGate.Running + " job is already running.");
			}
			_cts = new CancellationTokenSource();
			_status = new ScanStatus { State = "Running", Force = force, StartedUtc = DateTime.UtcNow, Message = "Starting" };
			CancellationToken token = _cts.Token;
			Task.Run(() => RunAsync(force, token, null, RunOrigin.Manual));
			return _status;
		}
	}

	/// <summary>
	/// Runs a scan of new and changed tracks inside Emby's scheduled task and waits for it. Skipped (and logged) when another job is running.
	/// Cancelling the task cancels the scan.
	/// </summary>
	public static async Task RunScheduledAsync(IProgress<double> progress, CancellationToken token)
	{
		CancellationToken own;
		lock (Gate)
		{
			if (!JobGate.TryEnter("scan"))
			{
				Plugin.Log("The scheduled scan was skipped: a " + JobGate.Running + " job is running.");
				return;
			}
			_cts = new CancellationTokenSource();
			_status = new ScanStatus { State = "Running", Force = false, StartedUtc = DateTime.UtcNow, Message = "Starting (scheduled)" };
			own = _cts.Token;
		}
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(own, token);
		await RunAsync(false, linked.Token, progress, RunOrigin.Scheduled).ConfigureAwait(false);
	}

	public static void Cancel()
	{
		lock (Gate)
		{
			_cts?.Cancel();
		}
	}

	private static void Update(Func<ScanStatus, ScanStatus> change)
	{
		lock (Gate)
		{
			_status = change(_status);
		}
	}

	private static async Task RunAsync(bool force, CancellationToken token, IProgress<double>? progress, RunOrigin origin)
	{
		int processed = 0;
		int scanned = 0;
		int unchanged = 0;
		int failed = 0;
		int total = 0;
		try
		{
			OnlineStage.ResetCounters();
			PluginConfiguration config = HallPassState.Config;
			DecisionSettings settings = HallPassState.DecisionSettingsFor(config);
			ProfanityScanner scanner = new ProfanityScanner(HallPassState.WordListFor(config), HallPassState.ScannerOptionsFor(config));
			string version = HallPassState.EvidenceVersionFor(config);
			EvidenceStore store = HallPassState.Store;

			BaseItem[] items = Plugin.Instance!.LibraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Audio" }, Recursive = true });
			total = items.Length;
			Update((ScanStatus s) => Copy(s, total: total, message: "Scanning " + total + " tracks"));

			foreach (BaseItem item in items)
			{
				token.ThrowIfCancellationRequested();
				try
				{
					TrackRecord? record = await TrackAnalyzer.AnalyzeAsync(item, store.Get(item.InternalId), config, scanner, version, force, token).ConfigureAwait(false);
					if (record == null)
					{
						unchanged++;
					}
					else
					{
						store.Upsert(record, settings);
						scanned++;
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					failed++;
					Plugin.Log("Could not scan " + item.Path + ": " + ex.GetType().Name + " " + ex.Message);
				}
				processed++;
				if (processed % 10 == 0)
				{
					int p = processed, sc = scanned, un = unchanged, fa = failed;
					Update((ScanStatus s) => Copy(s, processed: p, scanned: sc, unchanged: un, failed: fa));
					progress?.Report(total == 0 ? 100.0 : 100.0 * p / total);
					await Task.Yield();
				}
				if (processed % 200 == 0)
				{
					HallPassState.Save();
					HallPassState.SaveOnlineCache();
				}
			}

			int removed = store.RemoveAllExcept(new HashSet<long>(items.Select((BaseItem i) => i.InternalId)));
			HallPassState.Save();
			HallPassState.SaveOnlineCache();
			int pf = processed, sf = scanned, uf = unchanged, ff = failed;
			Update((ScanStatus s) => Copy(s, state: "Completed", processed: pf, scanned: sf, unchanged: uf, failed: ff, removed: removed, finished: true,
				message: "Done: " + sf + " scanned, " + uf + " unchanged" + (ff > 0 ? ", " + ff + " failed" : string.Empty) + (removed > 0 ? ", " + removed + " no longer in the library" : string.Empty)));
		}
		catch (OperationCanceledException)
		{
			HallPassState.Save();
			HallPassState.SaveOnlineCache();
			int p = processed, sc = scanned, un = unchanged, fa = failed;
			Update((ScanStatus s) => Copy(s, state: "Cancelled", processed: p, scanned: sc, unchanged: un, failed: fa, finished: true, message: "Cancelled; what was scanned is kept"));
		}
		catch (Exception ex)
		{
			Plugin.Log("The scan failed: " + ex.GetType().Name + " " + ex.Message);
			HallPassState.Save();
			Update((ScanStatus s) => Copy(s, state: "Failed", finished: true, message: "The scan failed: " + ex.Message));
		}
		finally
		{
			JobGate.Exit();
		}
		// the scan is over (finished, cancelled or failed): tell the phone app (never throws)
		NotificationHost.ScanEnded(Status, origin);
	}

	private static ScanStatus Copy(ScanStatus s, string? state = null, int? total = null, int? processed = null, int? scanned = null, int? unchanged = null, int? failed = null, int? removed = null, bool finished = false, string? message = null)
	{
		return new ScanStatus
		{
			State = state ?? s.State,
			Force = s.Force,
			Total = total ?? s.Total,
			Processed = processed ?? s.Processed,
			Scanned = scanned ?? s.Scanned,
			Unchanged = unchanged ?? s.Unchanged,
			Failed = failed ?? s.Failed,
			Removed = removed ?? s.Removed,
			OnlineAsked = OnlineStage.Counters.Asked,
			OnlineCached = OnlineStage.Counters.FromCache,
			OnlineFailed = OnlineStage.Counters.Failed,
			StartedUtc = s.StartedUtc,
			FinishedUtc = finished ? DateTime.UtcNow : s.FinishedUtc,
			Message = message ?? s.Message,
		};
	}
}
