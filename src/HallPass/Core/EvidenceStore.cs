using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HallPass.Core;

/// <summary>Everything stored about one track: where it is, what the scan found (never lyrics), and the decision.</summary>
public sealed class TrackRecord
{
	public long ItemId { get; set; }

	/// <summary>What the track looked like when it was scanned (path, size, time, lyric streams): a change means a rescan.</summary>
	public string FileKey { get; set; } = string.Empty;

	/// <summary>Fingerprint of the settings that shape the evidence (word list, scanner options, skip genres): a change means a rescan.</summary>
	public string EvidenceVersion { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string Artist { get; set; } = string.Empty;

	public string Album { get; set; } = string.Empty;

	public string Path { get; set; } = string.Empty;

	/// <summary>True for a track in a skipped genre.</summary>
	public bool Skipped { get; set; }

	public ManualOverride Override { get; set; }

	public Decision Decision { get; set; } = Decision.Unscanned;

	public double Confidence { get; set; }

	public ReviewReason ReviewReason { get; set; }

	public string Reason { get; set; } = string.Empty;

	public DateTime LastScannedUtc { get; set; }

	public List<EvidenceItem> Evidence { get; set; } = new List<EvidenceItem>();

	public TrackRecord Clone()
	{
		TrackRecord copy = (TrackRecord)MemberwiseClone();
		copy.Evidence = Evidence.ToList();
		return copy;
	}
}

/// <summary>A filtered, paged look at the stored tracks (the review queue).</summary>
public sealed class ReviewQuery
{
	public Decision[]? Decisions { get; set; }

	public ReviewReason[]? Reasons { get; set; }

	/// <summary>Matches the title, artist, album or path (any case).</summary>
	public string? Text { get; set; }

	/// <summary>Only tracks with evidence from this source ("lyrics", "title", "album", "folder", ...).</summary>
	public string? Source { get; set; }

	public bool OverridesOnly { get; set; }

	/// <summary>Highest confidence first instead of artist, album, title.</summary>
	public bool SortByConfidence { get; set; }

	public int Skip { get; set; }

	public int Take { get; set; } = 50;
}

public sealed class QueryResult
{
	public int Total { get; init; }

	public IReadOnlyList<TrackRecord> Items { get; init; } = Array.Empty<TrackRecord>();
}

public sealed class StoreStats
{
	public int Total { get; init; }

	public IReadOnlyDictionary<string, int> ByDecision { get; init; } = new Dictionary<string, int>();

	public IReadOnlyDictionary<string, int> ByReason { get; init; } = new Dictionary<string, int>();

	public int Overrides { get; init; }

	public DateTime? LastScanUtc { get; init; }
}

/// <summary>
/// The tracks known to Hall Pass and their decisions, in memory, saved as one JSON file by the plugin. Thread safe. A manual override is
/// never replaced by a scan. Pure logic apart from the JSON text (no Emby types, no files).
/// </summary>
public sealed class EvidenceStore
{
	private const int FormatVersion = 1;

	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
	{
		Converters = { new JsonStringEnumConverter() },
		WriteIndented = false,
	};

	private readonly object _gate = new object();

	private readonly Dictionary<long, TrackRecord> _items = new Dictionary<long, TrackRecord>();

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _items.Count;
			}
		}
	}

	public TrackRecord? Get(long itemId)
	{
		lock (_gate)
		{
			return _items.TryGetValue(itemId, out TrackRecord? r) ? r.Clone() : null;
		}
	}

	public IReadOnlyList<TrackRecord> All()
	{
		lock (_gate)
		{
			return _items.Values.Select((TrackRecord r) => r.Clone()).ToList();
		}
	}

	/// <summary>Stores a scanned track. Keeps a manual override that is already there and works out the decision.</summary>
	public TrackRecord Upsert(TrackRecord scanned, DecisionSettings settings)
	{
		lock (_gate)
		{
			TrackRecord record = scanned.Clone();
			if (_items.TryGetValue(record.ItemId, out TrackRecord? existing))
			{
				record.Override = existing.Override;
			}
			Redecide(record, settings);
			_items[record.ItemId] = record;
			return record.Clone();
		}
	}

	/// <summary>Sets or clears a manual decision for these tracks and works the decision out again. Returns how many tracks were known.</summary>
	public int SetOverride(IEnumerable<long> itemIds, ManualOverride manualOverride, DecisionSettings settings)
	{
		int changed = 0;
		lock (_gate)
		{
			foreach (long id in itemIds.Distinct())
			{
				if (_items.TryGetValue(id, out TrackRecord? record))
				{
					record.Override = manualOverride;
					Redecide(record, settings);
					changed++;
				}
			}
		}
		return changed;
	}

	/// <summary>Works every decision out again from the stored evidence (after the thresholds changed). No rescan needed.</summary>
	public void RedecideAll(DecisionSettings settings)
	{
		lock (_gate)
		{
			foreach (TrackRecord record in _items.Values)
			{
				Redecide(record, settings);
			}
		}
	}

	/// <summary>Forgets tracks that are no longer in the library. Returns how many were removed.</summary>
	public int RemoveAllExcept(ISet<long> keepIds)
	{
		lock (_gate)
		{
			List<long> gone = _items.Keys.Where((long id) => !keepIds.Contains(id)).ToList();
			foreach (long id in gone)
			{
				_items.Remove(id);
			}
			return gone.Count;
		}
	}

	public QueryResult Query(ReviewQuery query)
	{
		lock (_gate)
		{
			IEnumerable<TrackRecord> found = _items.Values;
			if (query.Decisions is { Length: > 0 })
			{
				found = found.Where((TrackRecord r) => query.Decisions.Contains(r.Decision));
			}
			if (query.Reasons is { Length: > 0 })
			{
				found = found.Where((TrackRecord r) => query.Reasons.Contains(r.ReviewReason));
			}
			if (query.OverridesOnly)
			{
				found = found.Where((TrackRecord r) => r.Override != ManualOverride.None);
			}
			if (!string.IsNullOrWhiteSpace(query.Source))
			{
				found = found.Where((TrackRecord r) => r.Evidence.Any((EvidenceItem e) => string.Equals(e.Source, query.Source, StringComparison.OrdinalIgnoreCase)));
			}
			if (!string.IsNullOrWhiteSpace(query.Text))
			{
				string text = query.Text.Trim();
				found = found.Where((TrackRecord r) => Contains(r.Name, text) || Contains(r.Artist, text) || Contains(r.Album, text) || Contains(r.Path, text));
			}
			List<TrackRecord> list = (query.SortByConfidence
				? found.OrderByDescending((TrackRecord r) => r.Confidence).ThenBy((TrackRecord r) => r.Artist, StringComparer.OrdinalIgnoreCase)
				: found.OrderBy((TrackRecord r) => r.Artist, StringComparer.OrdinalIgnoreCase).ThenBy((TrackRecord r) => r.Album, StringComparer.OrdinalIgnoreCase).ThenBy((TrackRecord r) => r.Name, StringComparer.OrdinalIgnoreCase))
				.ThenBy((TrackRecord r) => r.ItemId).ToList();
			int skip = Math.Max(0, query.Skip);
			int take = Math.Clamp(query.Take, 1, 500);
			return new QueryResult { Total = list.Count, Items = list.Skip(skip).Take(take).Select((TrackRecord r) => r.Clone()).ToList() };
		}
	}

	public StoreStats Stats()
	{
		lock (_gate)
		{
			return new StoreStats
			{
				Total = _items.Count,
				ByDecision = _items.Values.GroupBy((TrackRecord r) => r.Decision.ToString()).ToDictionary((IGrouping<string, TrackRecord> g) => g.Key, (IGrouping<string, TrackRecord> g) => g.Count()),
				ByReason = _items.Values.Where((TrackRecord r) => r.ReviewReason != ReviewReason.None).GroupBy((TrackRecord r) => r.ReviewReason.ToString()).ToDictionary((IGrouping<string, TrackRecord> g) => g.Key, (IGrouping<string, TrackRecord> g) => g.Count()),
				Overrides = _items.Values.Count((TrackRecord r) => r.Override != ManualOverride.None),
				LastScanUtc = _items.Count == 0 ? null : _items.Values.Max((TrackRecord r) => r.LastScannedUtc),
			};
		}
	}

	// ----- saving ----------------------------------------------------------------------------------------------

	public string ToJson()
	{
		lock (_gate)
		{
			return JsonSerializer.Serialize(new StoredFile { Version = FormatVersion, Items = _items.Values.OrderBy((TrackRecord r) => r.ItemId).ToList() }, Json);
		}
	}

	/// <summary>Reads a saved file. Empty or damaged text gives an empty store (the next scan fills it again).</summary>
	public static EvidenceStore FromJson(string? text)
	{
		EvidenceStore store = new EvidenceStore();
		if (string.IsNullOrWhiteSpace(text))
		{
			return store;
		}
		try
		{
			StoredFile? file = JsonSerializer.Deserialize<StoredFile>(text, Json);
			if (file?.Items != null)
			{
				foreach (TrackRecord record in file.Items)
				{
					store._items[record.ItemId] = record;
				}
			}
		}
		catch (JsonException)
		{
			return new EvidenceStore();
		}
		return store;
	}

	// ----- rescan logic -----------------------------------------------------------------------------------------

	/// <summary>A track is scanned again when it is new, forced, its file key changed, or the evidence settings changed.</summary>
	public static bool NeedsRescan(TrackRecord? existing, string fileKey, string evidenceVersion, bool force)
	{
		return force || existing == null || existing.FileKey != fileKey || existing.EvidenceVersion != evidenceVersion;
	}

	/// <summary>A short stable fingerprint of some text parts (changes when any part changes).</summary>
	public static string Fingerprint(params string?[] parts)
	{
		string joined = string.Join("\u001F", parts.Select((string? p) => p ?? string.Empty));
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
		return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
	}

	private static void Redecide(TrackRecord record, DecisionSettings settings)
	{
		DecisionResult result = DecisionEngine.Decide(record.Override, record.Evidence, settings, record.Skipped);
		record.Decision = result.Decision;
		record.Confidence = result.Confidence;
		record.ReviewReason = result.ReviewReason;
		record.Reason = result.Reason;
	}

	private static bool Contains(string? haystack, string needle) => haystack != null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

	private sealed class StoredFile
	{
		public int Version { get; set; }

		public List<TrackRecord> Items { get; set; } = new List<TrackRecord>();
	}
}
