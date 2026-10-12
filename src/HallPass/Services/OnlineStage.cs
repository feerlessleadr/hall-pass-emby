using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;

namespace HallPass.Services;

/// <summary>How many online questions a scan asked.</summary>
public sealed record OnlineCounters(int Asked, int FromCache, int Failed);

/// <summary>
/// Stage 2: asks Deezer (by ISRC, else by artist and title) and Apple's iTunes (by album barcode) whether a track is explicit. Opt-in. Sends only the
/// artist, title, ISRC or album barcode: never file names, paths or lyrics. Rate limited per provider, cached, retried on quota errors. A failure gives no
/// evidence (never "clean"). Reads only.
/// </summary>
internal static class OnlineStage
{
	private static readonly HttpClient Http = CreateClient();

	private static readonly TokenBucket DeezerBucket = new TokenBucket(8, 8);

	private static readonly TokenBucket ITunesBucket = new TokenBucket(2, 20.0 / 60.0);

	private static int _asked;

	private static int _cached;

	private static int _failed;

	public static OnlineCounters Counters => new OnlineCounters(Volatile.Read(ref _asked), Volatile.Read(ref _cached), Volatile.Read(ref _failed));

	public static void ResetCounters()
	{
		Volatile.Write(ref _asked, 0);
		Volatile.Write(ref _cached, 0);
		Volatile.Write(ref _failed, 0);
	}

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
		string version = typeof(OnlineStage).Assembly.GetName().Version?.ToString() ?? "0";
		client.DefaultRequestHeaders.UserAgent.ParseAdd("HallPass/" + version + " (Emby plugin; +https://github.com/feerlessleadr/hall-pass-emby)");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
		return client;
	}

	/// <summary>The evidence from the catalogs for one track (empty when online lookups are off or nothing was found).</summary>
	public static async Task<IReadOnlyList<EvidenceItem>> LookupAsync(TrackInfo local, TrackIdentifiers ids, PluginConfiguration config, CancellationToken token)
	{
		List<EvidenceItem> found = new List<EvidenceItem>();
		if (!config.OnlineEnabled)
		{
			return found;
		}
		if (config.OnlineDeezer)
		{
			IReadOnlyList<CandidateTrack>? candidates = null;
			if (ids.Isrc != null)
			{
				candidates = await FetchAsync("deezer-isrc:" + ids.Isrc, "https://api.deezer.com/track/isrc:" + ids.Isrc, DeezerParser.ParseTrack, DeezerBucket, config, token).ConfigureAwait(false);
			}
			if (candidates == null || candidates.Count == 0)
			{
				string terms = (local.Artist + " " + local.Title).Trim();
				// search by text when there is no ISRC, or the ISRC lookup answered "nothing found" (not when it failed)
				if (terms.Length > 0 && (ids.Isrc == null || candidates != null))
				{
					candidates = await FetchAsync("deezer-search:" + TrackMatcher.NormalizeArtist(local.Artist) + "|" + TrackMatcher.NormalizeTitle(local.Title),
						"https://api.deezer.com/search?limit=10&q=" + Uri.EscapeDataString(terms), DeezerParser.ParseList, DeezerBucket, config, token).ConfigureAwait(false);
				}
			}
			EvidenceItem? evidence = candidates == null ? null : OnlineMatcher.Evaluate(local, candidates, DeezerParser.Source);
			if (evidence != null)
			{
				found.Add(evidence);
			}
		}
		if (config.OnlineITunes && ids.Upc != null)
		{
			IReadOnlyList<CandidateTrack>? album = await FetchAsync("itunes-upc:" + ids.Upc, "https://itunes.apple.com/lookup?entity=song&upc=" + ids.Upc, ITunesParser.ParseLookup, ITunesBucket, config, token).ConfigureAwait(false);
			EvidenceItem? evidence = album == null ? null : OnlineMatcher.Evaluate(local, album, ITunesParser.Source);
			if (evidence != null)
			{
				found.Add(evidence);
			}
		}
		return found;
	}

	/// <summary>One question: from the cache when it is fresh, otherwise to the provider (waiting for its rate limit, retrying quota and server errors). Null means no usable answer.</summary>
	private static async Task<IReadOnlyList<CandidateTrack>?> FetchAsync(string key, string url, Func<string?, ProviderParse> parse, TokenBucket bucket, PluginConfiguration config, CancellationToken token)
	{
		OnlineCache cache = HallPassState.OnlineCache;
		if (cache.TryGet(key, DateTime.UtcNow, config.OnlineCacheDays, config.OnlineNegativeDays, out CachedLookup hit))
		{
			Interlocked.Increment(ref _cached);
			return hit.Candidates;
		}
		TimeSpan backoff = TimeSpan.FromSeconds(2);
		for (int attempt = 1; attempt <= 4; attempt++)
		{
			TimeSpan wait = bucket.Reserve();
			if (wait > TimeSpan.Zero)
			{
				await Task.Delay(wait, token).ConfigureAwait(false);
			}
			Interlocked.Increment(ref _asked);
			string? body = null;
			HttpStatusCode status = 0;
			TimeSpan? retryAfter = null;
			try
			{
				using HttpResponseMessage response = await Http.GetAsync(url, token).ConfigureAwait(false);
				status = response.StatusCode;
				retryAfter = response.Headers.RetryAfter?.Delta;
				body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
			{
				Plugin.Log("Online lookup failed (" + ex.GetType().Name + "), attempt " + attempt + ": " + key);
			}

			bool retryable = status == 0 || status == HttpStatusCode.TooManyRequests || (int)status >= 500;
			if (!retryable)
			{
				ProviderParse parsed = parse(body);
				switch (parsed.Status)
				{
				case ProviderStatus.Ok:
				case ProviderStatus.NotFound:
					cache.Put(key, parsed.Candidates, DateTime.UtcNow);
					return parsed.Candidates;
				case ProviderStatus.Failed:
					Interlocked.Increment(ref _failed);
					Plugin.Log("Online lookup gave an answer Hall Pass could not use (" + key + "): " + parsed.Message);
					return null;
				}
				// Retry: the provider's quota; wait and try again below
			}
			if (attempt == 4)
			{
				break;
			}
			TimeSpan pause = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(60, retryAfter.Value.TotalSeconds)) : backoff;
			await Task.Delay(pause, token).ConfigureAwait(false);
			backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
		}
		Interlocked.Increment(ref _failed);
		Plugin.Log("Online lookup gave up after several tries: " + key);
		return null;
	}
}
