using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;

namespace HallPass.Services;

/// <summary>
/// Connects the Emby-free notification logic (<see cref="EventAggregator"/>, <see cref="EventLog"/>) to Emby: where its files live, the saved
/// switches, and the push to administrator sessions. Nothing here may ever break a scan or a write job: every entry point catches and logs.
/// </summary>
internal static class NotificationHost
{
	private static readonly object Gate = new object();

	private static EventLog? _log;

	private static EventAggregator? _aggregator;

	private static string Folder => Plugin.Instance!.AppPaths.PluginConfigurationsPath;

	private static string StatePath => Path.Combine(Folder, "HallPass.Notifications.json");

	public static EventLog Events
	{
		get
		{
			lock (Gate)
			{
				return _log ??= new EventLog(Path.Combine(Folder, "HallPass.Events.json"));
			}
		}
	}

	private static EventAggregator Aggregator
	{
		get
		{
			lock (Gate)
			{
				if (_aggregator == null)
				{
					NotificationState? state = null;
					try
					{
						if (File.Exists(StatePath))
						{
							state = JsonSerializer.Deserialize<NotificationState>(File.ReadAllText(StatePath));
						}
					}
					catch (Exception ex) when (ex is IOException || ex is JsonException)
					{
						Plugin.Log("Could not read the saved alert state, starting fresh: " + ex.Message);
					}
					_aggregator = new EventAggregator(state ?? new NotificationState());
				}
				return _aggregator;
			}
		}
	}

	/// <summary>The "Send phone alerts" switches from the saved settings.</summary>
	public static NotificationSettings Settings()
	{
		PluginConfiguration c = HallPassState.Config;
		return new NotificationSettings
		{
			SendPhoneAlerts = c.SendPhoneAlerts,
			ScanFinished = c.AlertScanFinished,
			ScanFailed = c.AlertScanFailed,
			ApplyFinished = c.AlertApplyFinished,
			ApplyFailed = c.AlertApplyFailed,
			ReviewQueue = c.AlertReviewQueue,
		};
	}

	private static int ReviewCount()
	{
		return HallPassState.Store.Stats().ByDecision.TryGetValue(Decision.Review.ToString(), out int n) ? n : 0;
	}

	/// <summary>Called when the server has started: notes the review queue size and looks for jobs the server stopped in the middle of.</summary>
	public static void OnStart()
	{
		Guarded("starting", () =>
		{
			Emit(Aggregator.ReviewQueueChanged(ReviewCount(), DateTime.UtcNow, Settings(), true));
			List<UnfinishedJob> unfinished = JournalStore.List()
				.Where((JobSummary j) => j.Unfinished)
				.Select((JobSummary j) => new UnfinishedJob { JobId = j.JobId, Kind = j.Kind, StartedUtc = j.StartedUtc })
				.ToList();
			Emit(Aggregator.UnfinishedFound(unfinished, Settings()));
		});
	}

	/// <summary>A scan (the owner's or the scheduled task's) ended.</summary>
	public static void ScanEnded(ScanStatus status, RunOrigin origin)
	{
		Guarded("a finished scan", () =>
		{
			ScanOutcome outcome = new ScanOutcome
			{
				State = status.State,
				Origin = origin,
				Total = status.Total,
				Scanned = status.Scanned,
				Unchanged = status.Unchanged,
				Failed = status.Failed,
				OnlineFailed = status.OnlineFailed,
				NeedsReview = ReviewCount(),
				Message = status.Message,
			};
			Emit(Aggregator.ScanEnded(outcome, Settings()));
			if (status.State == "Completed")
			{
				Emit(Aggregator.ReviewQueueChanged(outcome.NeedsReview, DateTime.UtcNow, Settings(), false));
			}
		});
	}

	/// <summary>One batch of the automatic scan of new tracks is done.</summary>
	public static void AutoBatch(int total, int scanned, int unchanged, int failed, int onlineFailed)
	{
		Guarded("an automatic scan", () => Aggregator.AutoBatch(total, scanned, unchanged, failed, onlineFailed));
	}

	/// <summary>The automatic scan has nothing waiting any more: sends the one summary, if it was big enough.</summary>
	public static void AutoFlush()
	{
		Guarded("an automatic scan", () =>
		{
			if (!Aggregator.AutoPending)
			{
				return;
			}
			int review = ReviewCount();
			Emit(Aggregator.AutoFlush(review, Settings()));
			Emit(Aggregator.ReviewQueueChanged(review, DateTime.UtcNow, Settings(), false));
		});
	}

	/// <summary>A write job (apply, automatic apply or undo) ended.</summary>
	public static void WriteEnded(WriteStatus status, bool automatic)
	{
		Guarded("a finished write job", () =>
		{
			WriteOutcome outcome = new WriteOutcome
			{
				State = status.State,
				Kind = string.Equals(status.Kind, "undo", StringComparison.OrdinalIgnoreCase) ? "Undo" : "Apply",
				Automatic = automatic,
				Changed = status.Changed,
				Unchanged = status.Unchanged,
				Failed = status.Failed,
				Conflicts = status.Conflicts,
				Message = status.Message,
			};
			Emit(Aggregator.WriteEnded(outcome, Settings()));
		});
	}

	/// <summary>The owner changed decisions or settings: the review queue size is remembered without an alert.</summary>
	public static void ReviewBaseline()
	{
		Guarded("the review queue", () => Emit(Aggregator.ReviewQueueChanged(ReviewCount(), DateTime.UtcNow, Settings(), true)));
	}

	private static void Guarded(string what, Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Plugin.Log("Phone alerts for " + what + " hit a problem: " + ex.GetType().Name + " " + ex.Message);
		}
	}

	/// <summary>Logs the events (which gives them their numbers), saves the aggregator's memory and pushes them.</summary>
	private static void Emit(List<PendingEvent> pending)
	{
		lock (Gate)
		{
			try
			{
				string temp = StatePath + ".tmp";
				File.WriteAllText(temp, JsonSerializer.Serialize(Aggregator.State));
				if (File.Exists(StatePath))
				{
					File.Delete(StatePath);
				}
				File.Move(temp, StatePath);
			}
			catch (IOException ex)
			{
				Plugin.Log("Could not save the alert state: " + ex.Message);
			}
			foreach (PendingEvent e in pending)
			{
				Push(Events.Append(e));
			}
		}
	}

	/// <summary>Sends an event to every connected administrator session. Fire and forget: a failure is logged and never reaches the caller.</summary>
	private static void Push(NotificationEvent logged)
	{
		object? data = PlainJson.ToPlain(logged.Data);
		Task.Run(async () =>
		{
			try
			{
				await Plugin.Instance!.SessionManager.SendMessageToAdminSessions(logged.Name, data, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Plugin.Log("A phone alert could not be sent: " + ex.GetType().Name + " " + ex.Message);
			}
		});
	}
}
