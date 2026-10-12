using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;

namespace HallPass.Services;

/// <summary>
/// Fallback for tracks that have no lyrics of their own: asks LRCLIB (lrclib.net) for the lyrics by artist, title, album and length, scans them in memory and
/// keeps only the verdict (never the text). Opt-in, needs the online lookups switch too. Sends only artist, title, album and length. A failure gives no evidence.
/// </summary>
internal static class LyricsStage
{
	private static readonly HttpClient Http = CreateClient();

	private static readonly TokenBucket Bucket = new TokenBucket(3, 3);

	private static HttpClient CreateClient()
	{
		HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
		string version = typeof(LyricsStage).Assembly.GetName().Version?.ToString() ?? "0";
		client.DefaultRequestHeaders.UserAgent.ParseAdd("HallPass/" + version + " (Emby plugin; +https://github.com/feerlessleadr/hall-pass-emby)");
		client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
		return client;
	}

	/// <summary>The evidence from the online lyrics of one track, or null (switched off, not found, too little text, a failure).</summary>
	public static async Task<EvidenceItem?> LookupAsync(TrackInfo local, ProfanityScanner scanner, PluginConfiguration config, CancellationToken token)
	{
		if (!config.OnlineEnabled || !config.OnlineLyrics || string.IsNullOrWhiteSpace(local.Title) || string.IsNullOrWhiteSpace(local.Artist))
		{
			return null;
		}
		string url = "https://lrclib.net/api/get?artist_name=" + Uri.EscapeDataString(local.Artist) + "&track_name=" + Uri.EscapeDataString(local.Title)
			+ (string.IsNullOrWhiteSpace(local.Album) ? string.Empty : "&album_name=" + Uri.EscapeDataString(local.Album))
			+ (local.DurationSeconds.HasValue ? "&duration=" + Math.Round(local.DurationSeconds.Value) : string.Empty);
		LyricsAnswer? answer = await FetchAsync(url, token).ConfigureAwait(false);
		if (answer == null || !answer.Found || answer.Text == null || !OnlineLyrics.LengthsAgree(local.DurationSeconds, answer.DurationSeconds))
		{
			return null;
		}
		ScanResult result = scanner.Scan(answer.Text);
		EvidenceItem evidence = scanner.ToEvidence(result, OnlineLyrics.Source);
		return evidence.Verdict == Verdict.None ? null : OnlineLyrics.Capped(evidence);
	}

	private static async Task<LyricsAnswer?> FetchAsync(string url, CancellationToken token)
	{
		TimeSpan backoff = TimeSpan.FromSeconds(2);
		for (int attempt = 1; attempt <= 3; attempt++)
		{
			TimeSpan wait = Bucket.Reserve();
			if (wait > TimeSpan.Zero)
			{
				await Task.Delay(wait, token).ConfigureAwait(false);
			}
			HttpStatusCode status = 0;
			string? body = null;
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
				Plugin.Log("Online lyrics lookup failed (" + ex.GetType().Name + "), attempt " + attempt);
			}
			if (status == HttpStatusCode.NotFound)
			{
				return OnlineLyrics.NotFound;
			}
			if (status == HttpStatusCode.OK)
			{
				LyricsAnswer parsed = OnlineLyrics.Parse(body);
				if (parsed.Message != null)
				{
					Plugin.Log("Online lyrics gave an answer Hall Pass could not use: " + parsed.Message);
					return null;
				}
				return parsed;
			}
			bool retryable = status == 0 || status == HttpStatusCode.TooManyRequests || (int)status >= 500;
			if (!retryable || attempt == 3)
			{
				Plugin.Log("Online lyrics lookup gave up (HTTP " + (int)status + ").");
				return null;
			}
			TimeSpan pause = retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(60, retryAfter.Value.TotalSeconds)) : backoff;
			await Task.Delay(pause, token).ConfigureAwait(false);
			backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
		}
		return null;
	}
}
