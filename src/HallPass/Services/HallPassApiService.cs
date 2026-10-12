using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace HallPass.Services;

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
}

public class HallPassApiService : IService
{
	public object Get(StatusRequest request)
	{
		PluginConfiguration c = HallPassState.Config;
		StoreStats stats = HallPassState.Store.Stats();
		return new
		{
			Job = ScanJob.Status,
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
			SortByConfidence = string.Equals(request.Sort, "confidence", StringComparison.OrdinalIgnoreCase),
			Skip = request.Skip,
			Take = request.Take <= 0 ? 50 : request.Take,
		};
		QueryResult result = HallPassState.Store.Query(query);
		return new { result.Total, Items = result.Items.Select(Row).ToArray() };
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
		if (request.ItemIds == null || request.ItemIds.Length == 0)
		{
			throw new ArgumentException("Choose at least one track.");
		}
		if (request.ItemIds.Length > 5000)
		{
			throw new ArgumentException("At most 5000 tracks at a time.");
		}
		ManualOverride choice = (request.Decision ?? string.Empty).ToLowerInvariant() switch
		{
			"explicit" => ManualOverride.Explicit,
			"clean" => ManualOverride.Clean,
			"clearoverride" => ManualOverride.None,
			_ => throw new ArgumentException("Decision must be explicit, clean or clearOverride."),
		};
		int changed = HallPassState.Store.SetOverride(request.ItemIds, choice, HallPassState.DecisionSettingsFor(HallPassState.Config));
		HallPassState.Save();
		return new { Changed = changed };
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
