using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace HallPass.Services;

public sealed record AutoScanStatus(int Waiting, int Processed, DateTime? LastUtc);

/// <summary>
/// Looks at newly added tracks by itself. A library event only puts the track on a queue; a worker takes tracks in small batches once things have been
/// quiet for a while (a bulk import raises thousands of events). It records decisions the same way a scan does and, only when asked to
/// (apply after scan on, dry run off), writes the tags of those tracks. Never runs together with another job: it waits.
/// </summary>
internal static class AutoScan
{
	private const long QuietMs = 30_000;

	private const long MaxWaitMs = 300_000;

	private const int BatchSize = 50;

	private static readonly PendingQueue Queue = new PendingQueue();

	private static CancellationTokenSource? _cts;

	private static ILibraryManager? _library;

	private static int _processed;

	private static long _lastTicks;

	public static AutoScanStatus Status => new AutoScanStatus(Queue.Count, Volatile.Read(ref _processed), _lastTicks == 0 ? null : new DateTime(Interlocked.Read(ref _lastTicks), DateTimeKind.Utc));

	public static void Start()
	{
		Stop();
		_library = Plugin.Instance!.LibraryManager;
		_library.ItemAdded += OnItemAdded;
		_cts = new CancellationTokenSource();
		CancellationToken token = _cts.Token;
		Task.Run(() => WorkerAsync(token));
	}

	public static void Stop()
	{
		if (_library != null)
		{
			_library.ItemAdded -= OnItemAdded;
			_library = null;
		}
		_cts?.Cancel();
		_cts = null;
		Queue.Clear();
	}

	private static void OnItemAdded(object? sender, ItemChangeEventArgs e)
	{
		try
		{
			if (e.Item is Audio && Plugin.Instance!.Configuration.AutoScanNewTracks)
			{
				Queue.Add(e.Item.InternalId);
			}
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not note a new track: " + ex.GetType().Name + " " + ex.Message);
		}
	}

	private static async Task WorkerAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(5000, token).ConfigureAwait(false);
				IReadOnlyList<long> due = Queue.TakeDue(QuietMs, MaxWaitMs, BatchSize);
				if (due.Count == 0)
				{
					if (Queue.Count == 0)
					{
						NotificationHost.AutoFlush();
					}
					continue;
				}
				PluginConfiguration config = HallPassState.Config;
				if (!config.AutoScanNewTracks)
				{
					continue;
				}
				if (!JobGate.TryEnter("autoscan"))
				{
					Queue.Requeue(due); // another job is running: try again after a fresh quiet period
					continue;
				}
				List<long> looked = new List<long>();
				try
				{
					int onlineBefore = OnlineStage.Counters.Failed;
					(looked, int unchanged, int failed) = await ScanBatchAsync(due, config, token).ConfigureAwait(false);
					NotificationHost.AutoBatch(due.Count, looked.Count, unchanged, failed, OnlineStage.Counters.Failed - onlineBefore);
				}
				finally
				{
					JobGate.Exit();
				}
				if (Queue.Count == 0)
				{
					NotificationHost.AutoFlush(); // everything that was waiting is done: one summary, and only if it was a big batch
				}
				if (looked.Count > 0 && config.ApplyAfterScan && !config.DryRun)
				{
					try
					{
						WriteJob.StartAuto(looked);
					}
					catch (InvalidOperationException ex)
					{
						Plugin.Log("New tracks were scanned but their tags were not written: " + ex.Message);
					}
				}
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				Plugin.Log("The automatic scan of new tracks hit a problem: " + ex.GetType().Name + " " + ex.Message);
			}
		}
	}

	private static async Task<(List<long> Looked, int Unchanged, int Failed)> ScanBatchAsync(IReadOnlyList<long> ids, PluginConfiguration config, CancellationToken token)
	{
		DecisionSettings settings = HallPassState.DecisionSettingsFor(config);
		ProfanityScanner scanner = new ProfanityScanner(HallPassState.WordListFor(config), HallPassState.ScannerOptionsFor(config));
		string version = HallPassState.EvidenceVersionFor(config);
		EvidenceStore store = HallPassState.Store;
		List<long> looked = new List<long>();
		int unchanged = 0;
		int failed = 0;
		foreach (long id in ids)
		{
			token.ThrowIfCancellationRequested();
			if (Plugin.Instance!.LibraryManager.GetItemById(id) is not Audio item)
			{
				continue;
			}
			try
			{
				TrackRecord? record = await TrackAnalyzer.AnalyzeAsync(item, store.Get(id), config, scanner, version, false, token).ConfigureAwait(false);
				if (record != null)
				{
					store.Upsert(record, settings);
					looked.Add(id);
				}
				else
				{
					unchanged++;
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				failed++;
				Plugin.Log("Could not scan the new track " + item.Path + ": " + ex.GetType().Name + " " + ex.Message);
			}
		}
		HallPassState.Save();
		HallPassState.SaveOnlineCache();
		Interlocked.Add(ref _processed, ids.Count);
		Interlocked.Exchange(ref _lastTicks, DateTime.UtcNow.Ticks);
		if (looked.Count > 0)
		{
			Plugin.Log("Scanned " + looked.Count + " new track(s) automatically.");
		}
		return (looked, unchanged, failed);
	}
}
