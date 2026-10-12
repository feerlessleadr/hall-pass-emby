using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace HallPass.Services;

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Info", "GET", Summary = "Plugin name, version and API version.")]
public class InfoRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Status", "GET", Summary = "The scan job, the counts and the active settings.")]
public class StatusRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Scan", "POST", Summary = "Starts a scan (a dry run: decisions are recorded, no tag is written).")]
public class StartScanRequest : IReturn<object>
{
	/// <summary>Look at every track again, even the ones that have not changed.</summary>
	public bool Force { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Scan/Cancel", "POST", Summary = "Stops the running scan; what was scanned is kept.")]
public class CancelScanRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Review", "GET", Summary = "The review queue: stored tracks with filters and paging.")]
public class ReviewRequest : IReturn<object>
{
	/// <summary>Comma separated: Explicit, Clean, LikelyClean, Review, Skipped.</summary>
	public string Decision { get; set; }

	/// <summary>Comma separated: NoEvidence, Suspected, Conflict, WeakClean.</summary>
	public string Reason { get; set; }

	public string Text { get; set; }

	public string Source { get; set; }

	public bool Overrides { get; set; }

	/// <summary>Only this artist (exact, any case). With Album it picks out one album.</summary>
	public string Artist { get; set; }

	/// <summary>Only this album (exact, any case).</summary>
	public string Album { get; set; }

	/// <summary>Only tracks that have no album.</summary>
	public bool NoAlbum { get; set; }

	/// <summary>"confidence" for highest confidence first; anything else sorts by artist, album, title.</summary>
	public string Sort { get; set; }

	public int Skip { get; set; }

	public int Take { get; set; } = 50;
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Items/{Id}", "GET", Summary = "One stored track with all its evidence.")]
public class TrackRequest : IReturn<object>
{
	public string Id { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Items/Decide", "POST", Summary = "Sets or clears a manual decision for tracks.")]
public class DecideRequest : IReturn<object>
{
	public long[] ItemIds { get; set; }

	/// <summary>"explicit", "clean" or "clearOverride".</summary>
	public string Decision { get; set; }

	/// <summary>True: ignore ItemIds and act on every track that matches the Match... filters (the review list's filters; none at all means every track).</summary>
	public bool AllMatching { get; set; }

	public string MatchDecision { get; set; }

	public string MatchReason { get; set; }

	public string MatchText { get; set; }

	public string MatchSource { get; set; }

	public bool MatchOverrides { get; set; }

	public string MatchArtist { get; set; }

	public string MatchAlbum { get; set; }

	public bool MatchNoAlbum { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Review/Albums", "GET", Summary = "The review list grouped by album: matching and total tracks per album.")]
public class AlbumsRequest : IReturn<object>
{
	public string Decision { get; set; }

	public string Reason { get; set; }

	public string Text { get; set; }

	public string Source { get; set; }

	public bool Overrides { get; set; }

	public int Skip { get; set; }

	public int Take { get; set; } = 50;
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Apply/Preview", "GET", Summary = "What 'Apply' would change: counts per tag and a sample. Always available, writes nothing.")]
public class PreviewRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Apply", "POST", Summary = "Writes the tags from the stored decisions (refused while dry run is on).")]
public class ApplyRequest : IReturn<object>
{
	/// <summary>Must be true: a deliberate click.</summary>
	public bool Confirm { get; set; }

	/// <summary>Needed as well when more than 5000 tracks would change.</summary>
	public bool ConfirmLarge { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Write/Cancel", "POST", Summary = "Stops a running apply or undo; what was saved stays (and is in the journal).")]
public class CancelWriteRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Jobs", "GET", Summary = "The journal: apply and undo jobs, newest first.")]
public class JobsRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Jobs/{JobId}/Undo", "POST", Summary = "Puts back the tags a job changed (tracks edited since are left alone unless forced).")]
public class UndoRequest : IReturn<object>
{
	public string JobId { get; set; }

	public bool Force { get; set; }
}

public class HallPassApiService : IService
{
	public object Get(InfoRequest request)
	{
		return new
		{
			Name = Plugin.Instance.Name,
			Version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
			ApiVersion = 1
		};
	}

	public object Get(StatusRequest request)
	{
		PluginConfiguration c = HallPassState.Config;
		StoreStats stats = HallPassState.Store.Stats();
		return new
		{
			Job = ScanJob.Status,
			Write = WriteJob.Status,
			Auto = AutoScan.Status,
			Stats = new
			{
				stats.Total,
				// plain dictionaries: Emby writes a read-only dictionary as a list of pairs
				ByDecision = new Dictionary<string, int>(stats.ByDecision),
				ByReason = new Dictionary<string, int>(stats.ByReason),
				stats.Overrides,
				stats.LastScanUtc
			},
			c.DryRun,
			c.Preset,
			Version = typeof(Plugin).Assembly.GetName().Version?.ToString()
		};
	}

	public object Post(StartScanRequest request)
	{
		try
		{
			return ScanJob.Start(request.Force);
		}
		catch (InvalidOperationException ex)
		{
			throw new ArgumentException(ex.Message);
		}
	}

	public object Post(CancelScanRequest request)
	{
		ScanJob.Cancel();
		return ScanJob.Status;
	}

	public object Get(ReviewRequest request)
	{
		ReviewQuery query = new ReviewQuery
		{
			Decisions = ParseEnums<Decision>(request.Decision),
			Reasons = ParseEnums<ReviewReason>(request.Reason),
			Text = request.Text,
			Source = request.Source,
			OverridesOnly = request.Overrides,
			Artist = string.IsNullOrEmpty(request.Artist) ? null : request.Artist,
			Album = request.NoAlbum ? string.Empty : (string.IsNullOrEmpty(request.Album) ? null : request.Album),
			SortByConfidence = string.Equals(request.Sort, "confidence", StringComparison.OrdinalIgnoreCase),
			Skip = request.Skip,
			Take = request.Take <= 0 ? 50 : request.Take,
		};
		QueryResult result = HallPassState.Store.Query(query);
		return new { result.Total, Items = result.Items.Select(Row).ToArray() };
	}

	public object Get(AlbumsRequest request)
	{
		ReviewQuery query = new ReviewQuery
		{
			Decisions = ParseEnums<Decision>(request.Decision),
			Reasons = ParseEnums<ReviewReason>(request.Reason),
			Text = request.Text,
			Source = request.Source,
			OverridesOnly = request.Overrides,
			Skip = request.Skip,
			Take = request.Take <= 0 ? 50 : request.Take,
		};
		AlbumGroupResult result = HallPassState.Store.Albums(query);
		return new
		{
			result.Total,
			Items = result.Items.Select((AlbumGroup g) => new { g.Artist, g.Album, g.Matching, g.Total, ByDecision = new Dictionary<string, int>(g.ByDecision) }).ToArray()
		};
	}

	public object Get(TrackRequest request)
	{
		if (!long.TryParse(request.Id, out long id))
		{
			throw new ArgumentException("Id must be Emby's internal item id (a number).");
		}
		TrackRecord record = HallPassState.Store.Get(id) ?? throw new ArgumentException("That track has not been scanned.");
		return new
		{
			Id = record.ItemId,
			record.Name,
			record.Artist,
			record.Album,
			record.Path,
			Decision = record.Decision.ToString(),
			record.Confidence,
			ReviewReason = record.ReviewReason.ToString(),
			record.Reason,
			Override = record.Override.ToString(),
			record.Skipped,
			LastScannedUtc = record.LastScannedUtc,
			Evidence = record.Evidence.Select((EvidenceItem e) => new { e.Source, Verdict = e.Verdict.ToString(), e.Confidence, e.Detail }).ToArray()
		};
	}

	public object Post(DecideRequest request)
	{
		ManualOverride choice = (request.Decision ?? string.Empty).ToLowerInvariant() switch
		{
			"explicit" => ManualOverride.Explicit,
			"clean" => ManualOverride.Clean,
			"clearoverride" => ManualOverride.None,
			_ => throw new ArgumentException("Decision must be explicit, clean or clearOverride."),
		};
		IReadOnlyList<long> ids;
		bool truncated = false;
		if (request.AllMatching)
		{
			ReviewQuery query = new ReviewQuery
			{
				Decisions = ParseEnums<Decision>(request.MatchDecision),
				Reasons = ParseEnums<ReviewReason>(request.MatchReason),
				Text = request.MatchText,
				Source = request.MatchSource,
				OverridesOnly = request.MatchOverrides,
				Artist = string.IsNullOrEmpty(request.MatchArtist) ? null : request.MatchArtist,
				Album = request.MatchNoAlbum ? string.Empty : (string.IsNullOrEmpty(request.MatchAlbum) ? null : request.MatchAlbum),
			};
			ids = HallPassState.Store.MatchingIds(query, 20000, out truncated);
			if (ids.Count == 0)
			{
				throw new ArgumentException("No track matches.");
			}
		}
		else
		{
			if (request.ItemIds == null || request.ItemIds.Length == 0)
			{
				throw new ArgumentException("Choose at least one track.");
			}
			if (request.ItemIds.Length > 5000)
			{
				throw new ArgumentException("At most 5000 tracks at a time.");
			}
			ids = request.ItemIds;
		}
		int changed = HallPassState.Store.SetOverride(ids, choice, HallPassState.DecisionSettingsFor(HallPassState.Config));
		HallPassState.Save();
		return new { Changed = changed, Matched = ids.Count, Truncated = truncated };
	}

	public object Get(PreviewRequest request)
	{
		return BuildPreview(50);
	}

	public object Post(ApplyRequest request)
	{
		if (!request.Confirm)
		{
			throw new ArgumentException("Confirm must be true.");
		}
		PreviewResult preview = BuildPreview(0);
		if (preview.WouldChange == 0)
		{
			return new { Started = false, Message = "Nothing to change: every track already has the right tag.", Write = WriteJob.Status };
		}
		if (preview.WouldChange > 5000 && !request.ConfirmLarge)
		{
			throw new ArgumentException(preview.WouldChange + " tracks would change. Confirm again to write that many.");
		}
		try
		{
			return new { Started = true, Message = "Started", Write = WriteJob.StartApply() };
		}
		catch (InvalidOperationException ex)
		{
			throw new ArgumentException(ex.Message);
		}
	}

	public object Post(CancelWriteRequest request)
	{
		WriteJob.Cancel();
		return WriteJob.Status;
	}

	public object Get(JobsRequest request)
	{
		return JournalStore.List().Select((JobSummary j) => new
		{
			j.JobId,
			j.Kind,
			j.StartedUtc,
			j.FinishedUtc,
			j.Unfinished,
			j.Changed,
			j.Unchanged,
			j.Failed,
			j.Conflicts,
			j.Cancelled,
			j.UndoOf,
			j.UndoneBy
		}).ToArray();
	}

	public object Post(UndoRequest request)
	{
		if (!JournalStore.IsValidId(request.JobId))
		{
			throw new ArgumentException("That is not a job id.");
		}
		try
		{
			return WriteJob.StartUndo(request.JobId, request.Force);
		}
		catch (InvalidOperationException ex)
		{
			throw new ArgumentException(ex.Message);
		}
	}

	private sealed class PreviewResult
	{
		public int Total { get; set; }

		public int WouldChange { get; set; }

		public int Unchanged { get; set; }

		public int Missing { get; set; }

		public Dictionary<string, int> ByTag { get; } = new Dictionary<string, int>();

		public List<object> Sample { get; } = new List<object>();

		public bool DryRun { get; set; }
	}

	private static PreviewResult BuildPreview(int sampleSize)
	{
		PluginConfiguration c = HallPassState.Config;
		TagNames names = WriteJob.NamesFor(c);
		PreviewResult result = new PreviewResult { DryRun = c.DryRun };
		foreach (TrackRecord record in HallPassState.Store.All().Where((TrackRecord r) => r.Decision != Decision.Unscanned))
		{
			result.Total++;
			MediaBrowser.Controller.Entities.BaseItem item = Plugin.Instance.LibraryManager.GetItemById(record.ItemId);
			if (item == null)
			{
				result.Missing++;
				continue;
			}
			TagChange change = TagPlan.ForDecision(item.Tags, names, record.Decision, c.TagSkippedAsClean);
			if (!change.Changed)
			{
				result.Unchanged++;
				continue;
			}
			result.WouldChange++;
			string target = names.For(record.Decision, c.TagSkippedAsClean) ?? "(no tag)";
			result.ByTag[target] = result.ByTag.TryGetValue(target, out int n) ? n + 1 : 1;
			if (result.Sample.Count < sampleSize)
			{
				result.Sample.Add(new { Id = record.ItemId, record.Name, record.Artist, Decision = record.Decision.ToString(), change.Before, change.After });
			}
		}
		return result;
	}

	private static T[] ParseEnums<T>(string value) where T : struct, Enum
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		List<T> found = new List<T>();
		foreach (string part in value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
		{
			if (Enum.TryParse(part.Trim(), true, out T parsed))
			{
				found.Add(parsed);
			}
		}
		return found.Count == 0 ? null : found.ToArray();
	}

	private static object Row(TrackRecord r)
	{
		return new
		{
			Id = r.ItemId,
			r.Name,
			r.Artist,
			r.Album,
			Decision = r.Decision.ToString(),
			r.Confidence,
			ReviewReason = r.ReviewReason.ToString(),
			r.Reason,
			Override = r.Override.ToString(),
			r.Skipped,
			Evidence = r.Evidence.Select((EvidenceItem e) => e.Source + ": " + e.Verdict + " " + e.Confidence.ToString("0.00") + (string.IsNullOrEmpty(e.Detail) ? string.Empty : " (" + e.Detail + ")")).ToArray()
		};
	}
}
