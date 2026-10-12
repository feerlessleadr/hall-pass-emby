using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HallPass.Core;

/// <summary>One track as an online catalog describes it.</summary>
public sealed record CandidateTrack(string Title, string Artist, string? Album, double? DurationSeconds, string? Isrc, ExplicitFlag Flag, string Source);

public enum ProviderStatus
{
	/// <summary>Answered, with at least one track.</summary>
	Ok,

	/// <summary>Answered: nothing matches.</summary>
	NotFound,

	/// <summary>A quota or a temporary problem: try again later.</summary>
	Retry,

	/// <summary>Anything else that went wrong (unreadable answer, a refusal).</summary>
	Failed,
}

public sealed record ProviderParse(ProviderStatus Status, IReadOnlyList<CandidateTrack> Candidates, string? Message = null)
{
	public static ProviderParse NotFound => new ProviderParse(ProviderStatus.NotFound, Array.Empty<CandidateTrack>());
}

/// <summary>Reads Deezer's public API answers (track by ISRC, search, album by barcode). Pure logic.</summary>
public static class DeezerParser
{
	public const string Source = "deezer";

	public static ProviderParse ParseTrack(string? json)
	{
		return Parse(json, single: true);
	}

	public static ProviderParse ParseList(string? json)
	{
		return Parse(json, single: false);
	}

	private static ProviderParse Parse(string? json, bool single)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), "empty answer");
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), "not an object");
			}
			// Deezer answers errors with HTTP 200 and an "error" object: code 4 is its quota, 800 means no data.
			if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
			{
				int code = error.TryGetProperty("code", out JsonElement c) && c.TryGetInt32(out int n) ? n : 0;
				string message = error.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? string.Empty : string.Empty;
				return code switch
				{
					800 => ProviderParse.NotFound,
					4 or 100 => new ProviderParse(ProviderStatus.Retry, Array.Empty<CandidateTrack>(), message),
					_ => new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), "error " + code + ": " + message),
				};
			}
			List<CandidateTrack> found = new List<CandidateTrack>();
			if (single)
			{
				CandidateTrack? one = ReadTrack(root);
				if (one != null)
				{
					found.Add(one);
				}
			}
			else if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in data.EnumerateArray())
				{
					CandidateTrack? track = ReadTrack(item);
					if (track != null)
					{
						found.Add(track);
					}
				}
			}
			return found.Count == 0 ? ProviderParse.NotFound : new ProviderParse(ProviderStatus.Ok, found);
		}
		catch (JsonException ex)
		{
			return new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), ex.Message);
		}
	}

	private static CandidateTrack? ReadTrack(JsonElement e)
	{
		if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("title", out JsonElement title) || title.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		string artist = e.TryGetProperty("artist", out JsonElement a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out JsonElement an) ? an.GetString() ?? string.Empty : string.Empty;
		string? album = e.TryGetProperty("album", out JsonElement al) && al.ValueKind == JsonValueKind.Object && al.TryGetProperty("title", out JsonElement at) ? at.GetString() : null;
		double? duration = e.TryGetProperty("duration", out JsonElement d) && d.TryGetDouble(out double seconds) && seconds > 0 ? seconds : null;
		string? isrc = e.TryGetProperty("isrc", out JsonElement i) && i.ValueKind == JsonValueKind.String ? TrackMatcher.NormalizeIsrc(i.GetString()) : null;
		int? code = e.TryGetProperty("explicit_content_lyrics", out JsonElement x) && x.TryGetInt32(out int xv) ? xv : null;
		bool? flag = e.TryGetProperty("explicit_lyrics", out JsonElement f) && (f.ValueKind == JsonValueKind.True || f.ValueKind == JsonValueKind.False) ? f.GetBoolean() : null;
		return new CandidateTrack(title.GetString() ?? string.Empty, artist, album, duration, isrc, FlagFrom(code, flag), Source);
	}

	/// <summary>
	/// Deezer's <c>explicit_content_lyrics</c>: 0 not explicit, 1 explicit, 2 unknown, 3 edited (a cleaned version), 4 partially unknown,
	/// 5 not applicable. Without that code, only "true" in <c>explicit_lyrics</c> is taken (false can just mean unknown).
	/// </summary>
	public static ExplicitFlag FlagFrom(int? code, bool? explicitLyrics)
	{
		if (code.HasValue)
		{
			return code.Value switch
			{
				1 => ExplicitFlag.Explicit,
				0 or 3 => ExplicitFlag.Clean,
				_ => ExplicitFlag.Unknown,
			};
		}
		return explicitLyrics == true ? ExplicitFlag.Explicit : ExplicitFlag.Unknown;
	}
}

/// <summary>Reads the iTunes lookup answer for an album barcode (the album and each of its songs). Pure logic.</summary>
public static class ITunesParser
{
	public const string Source = "itunes";

	public static ProviderParse ParseLookup(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), "empty answer");
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
			{
				return new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), "no results list");
			}
			List<CandidateTrack> found = new List<CandidateTrack>();
			foreach (JsonElement r in results.EnumerateArray())
			{
				if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("wrapperType", out JsonElement w) || w.GetString() != "track" || !r.TryGetProperty("trackName", out JsonElement name))
				{
					continue;
				}
				string artist = r.TryGetProperty("artistName", out JsonElement a) ? a.GetString() ?? string.Empty : string.Empty;
				string? album = r.TryGetProperty("collectionName", out JsonElement c) ? c.GetString() : null;
				double? seconds = r.TryGetProperty("trackTimeMillis", out JsonElement t) && t.TryGetDouble(out double ms) && ms > 0 ? ms / 1000.0 : null;
				string? explicitness = r.TryGetProperty("trackExplicitness", out JsonElement x) ? x.GetString() : null;
				found.Add(new CandidateTrack(name.GetString() ?? string.Empty, artist, album, seconds, null, FlagFrom(explicitness), Source));
			}
			return found.Count == 0 ? ProviderParse.NotFound : new ProviderParse(ProviderStatus.Ok, found);
		}
		catch (JsonException ex)
		{
			return new ProviderParse(ProviderStatus.Failed, Array.Empty<CandidateTrack>(), ex.Message);
		}
	}

	/// <summary>iTunes <c>trackExplicitness</c>: explicit, cleaned (a censored version) or notExplicit.</summary>
	public static ExplicitFlag FlagFrom(string? trackExplicitness)
	{
		return trackExplicitness switch
		{
			"explicit" => ExplicitFlag.Explicit,
			"cleaned" or "notExplicit" => ExplicitFlag.Clean,
			_ => ExplicitFlag.Unknown,
		};
	}
}

/// <summary>Turns the tracks an online catalog returned into evidence about one local track, strictly and with the careful side winning. Pure logic.</summary>
public static class OnlineMatcher
{
	/// <summary>An online "not explicit" is weak evidence (catalogs often miss old songs), so a single one never reaches the automatic clean bar.</summary>
	public const double CleanCap = 0.85;

	/// <summary>Below this a match is ignored.</summary>
	public const double MinimumMatch = 0.5;

	public static EvidenceItem? Evaluate(TrackInfo local, IReadOnlyList<CandidateTrack> candidates, string source)
	{
		List<(CandidateTrack Candidate, MatchResult Match)> scored = candidates
			.Select((CandidateTrack c) => (Candidate: c, Match: TrackMatcher.Match(local, new TrackInfo(c.Title, c.Artist, c.Album, c.DurationSeconds, c.Isrc))))
			.Where(((CandidateTrack Candidate, MatchResult Match) s) => s.Match.Confidence >= MinimumMatch)
			.OrderByDescending(((CandidateTrack Candidate, MatchResult Match) s) => s.Match.Confidence)
			.ToList();
		if (scored.Count == 0)
		{
			return null;
		}
		double best = scored[0].Match.Confidence;
		// Equal best matches that disagree: the explicit one wins. A strong match (0.8 or more) that says explicit also wins over a better clean one.
		(CandidateTrack Candidate, MatchResult Match)? explicitHit = scored.Where(((CandidateTrack Candidate, MatchResult Match) s) => s.Candidate.Flag == ExplicitFlag.Explicit && (s.Match.Confidence >= best - 0.001 || s.Match.Confidence >= TrackMatcher.TitleArtistDurationMatch)).Cast<(CandidateTrack Candidate, MatchResult Match)?>().FirstOrDefault();
		(CandidateTrack Candidate, MatchResult Match) chosen = explicitHit ?? scored[0];
		switch (chosen.Candidate.Flag)
		{
		case ExplicitFlag.Explicit:
			return new EvidenceItem(source, Verdict.Explicit, chosen.Match.Confidence, Describe(chosen, "explicit"));
		case ExplicitFlag.Clean:
			return new EvidenceItem(source, Verdict.Clean, Math.Min(chosen.Match.Confidence, CleanCap), Describe(chosen, "not explicit"));
		default:
			return null;
		}
	}

	private static string Describe((CandidateTrack Candidate, MatchResult Match) hit, string says)
	{
		return says + " (" + hit.Match.Reason + (hit.Candidate.Album != null ? ", album " + hit.Candidate.Album : string.Empty) + ")";
	}
}

/// <summary>Keeps a provider's requests under its rate limit: a bucket that fills at a steady rate. Asks how long to wait before the next request. Thread safe.</summary>
public sealed class TokenBucket
{
	private readonly object _gate = new object();

	private readonly double _capacity;

	private readonly double _perMillisecond;

	private readonly Func<long> _nowMs;

	private double _tokens;

	private long _last;

	public TokenBucket(double capacity, double refillPerSecond, Func<long>? nowMs = null)
	{
		_capacity = capacity;
		_perMillisecond = refillPerSecond / 1000.0;
		_nowMs = nowMs ?? (() => Environment.TickCount64);
		_tokens = capacity;
		_last = _nowMs();
	}

	/// <summary>Takes one request's worth and returns how long to wait before sending it (zero when one is free).</summary>
	public TimeSpan Reserve()
	{
		lock (_gate)
		{
			long now = _nowMs();
			_tokens = Math.Min(_capacity, _tokens + (now - _last) * _perMillisecond);
			_last = now;
			_tokens -= 1;
			return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Ceiling(-_tokens / _perMillisecond));
		}
	}
}

/// <summary>What was asked of a catalog and what came back, kept so a re-run does not ask again. Entries expire; "nothing found" expires sooner.</summary>
public sealed class CachedLookup
{
	public string Key { get; set; } = string.Empty;

	public DateTime FetchedUtc { get; set; }

	public bool Found { get; set; }

	public List<CandidateTrack> Candidates { get; set; } = new List<CandidateTrack>();
}

public sealed class OnlineCache
{
	private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

	private readonly object _gate = new object();

	private readonly Dictionary<string, CachedLookup> _items = new Dictionary<string, CachedLookup>(StringComparer.Ordinal);

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

	/// <summary>The cached answer for a key, unless it is older than <paramref name="positiveDays"/> (found) or <paramref name="negativeDays"/> (nothing found).</summary>
	public bool TryGet(string key, DateTime nowUtc, int positiveDays, int negativeDays, out CachedLookup entry)
	{
		lock (_gate)
		{
			if (_items.TryGetValue(key, out CachedLookup? e) && nowUtc - e.FetchedUtc <= TimeSpan.FromDays(e.Found ? positiveDays : negativeDays))
			{
				entry = e;
				return true;
			}
		}
		entry = new CachedLookup();
		return false;
	}

	public void Put(string key, IReadOnlyList<CandidateTrack> candidates, DateTime nowUtc)
	{
		lock (_gate)
		{
			_items[key] = new CachedLookup { Key = key, FetchedUtc = nowUtc, Found = candidates.Count > 0, Candidates = candidates.ToList() };
		}
	}

	/// <summary>Drops entries older than the longest time anything is kept. Returns how many.</summary>
	public int Prune(DateTime nowUtc, int maxDays)
	{
		lock (_gate)
		{
			List<string> old = _items.Where((KeyValuePair<string, CachedLookup> kv) => nowUtc - kv.Value.FetchedUtc > TimeSpan.FromDays(maxDays)).Select((KeyValuePair<string, CachedLookup> kv) => kv.Key).ToList();
			foreach (string key in old)
			{
				_items.Remove(key);
			}
			return old.Count;
		}
	}

	public string ToJson()
	{
		lock (_gate)
		{
			return JsonSerializer.Serialize(_items.Values.OrderBy((CachedLookup e) => e.Key, StringComparer.Ordinal).ToList(), Json);
		}
	}

	public static OnlineCache FromJson(string? text)
	{
		OnlineCache cache = new OnlineCache();
		if (string.IsNullOrWhiteSpace(text))
		{
			return cache;
		}
		try
		{
			List<CachedLookup>? list = JsonSerializer.Deserialize<List<CachedLookup>>(text, Json);
			foreach (CachedLookup e in list ?? new List<CachedLookup>())
			{
				if (!string.IsNullOrEmpty(e.Key))
				{
					cache._items[e.Key] = e;
				}
			}
		}
		catch (JsonException)
		{
			return new OnlineCache();
		}
		return cache;
	}
}
