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
			Task.Run(() => RunAsync(force, token));
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

	private static void Update(Func<ScanStatus, ScanStatus> change)
	{
		lock (Gate)
		{
			_status = change(_status);
		}
	}

	private static async Task RunAsync(bool force, CancellationToken token)
	{
		int processed = 0;
		int scanned = 0;
		int unchanged = 0;
		int failed = 0;
		int total = 0;
		try
		{
			PluginConfiguration config = HallPassState.Config;
			DecisionSettings settings = HallPassState.DecisionSettingsFor(config);
			ProfanityScanner scanner = new ProfanityScanner(WordList.Default(), HallPassState.ScannerOptionsFor(config));
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
					await Task.Yield();
				}
				if (processed % 200 == 0)
				{
					HallPassState.Save();
				}
			}

			int removed = store.RemoveAllExcept(new HashSet<long>(items.Select((BaseItem i) => i.InternalId)));
			HallPassState.Save();
			int pf = processed, sf = scanned, uf = unchanged, ff = failed;
			Update((ScanStatus s) => Copy(s, state: "Completed", processed: pf, scanned: sf, unchanged: uf, failed: ff, removed: removed, finished: true,
				message: "Done: " + sf + " scanned, " + uf + " unchanged" + (ff > 0 ? ", " + ff + " failed" : string.Empty) + (removed > 0 ? ", " + removed + " no longer in the library" : string.Empty)));
		}
		catch (OperationCanceledException)
		{
			HallPassState.Save();
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
			StartedUtc = s.StartedUtc,
			FinishedUtc = finished ? DateTime.UtcNow : s.FinishedUtc,
			Message = message ?? s.Message,
		};
	}
}
