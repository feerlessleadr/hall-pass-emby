using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace HallPass.Services;

[Authenticated(Roles = "Admin")]
[Route("/HallPass/WordList", "GET", Summary = "The built-in word list (tiers, stems, allowed words) and the owner's changes.")]
public class WordListRequest : IReturn<object>
{
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/WordList/Test", "POST", Summary = "Scores a piece of text with the built-in list plus the given (possibly unsaved) changes. Nothing is stored.")]
public class WordListTestRequest : IReturn<object>
{
	public string Text { get; set; }

	public string[] ExtraSevereWords { get; set; }

	public string[] ExtraModerateWords { get; set; }

	public string[] ExtraMildWords { get; set; }

	public string[] ExtraAllowedWords { get; set; }

	public string[] IgnoredWords { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Lookup", "POST", Summary = "Asks the online sources about one track you describe (nothing is stored in the library). Contacts Deezer, iTunes and LRCLIB on request.")]
public class LookupRequest : IReturn<object>
{
	public string Artist { get; set; }

	public string Title { get; set; }

	public string Album { get; set; }

	public double Seconds { get; set; }

	public string Isrc { get; set; }

	public string Upc { get; set; }
}

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Items/Rescan", "POST", Summary = "Looks at one track again (ignores what was recorded) and records the new decision.")]
public class RescanRequest : IReturn<object>
{
	public long ItemId { get; set; }
}

/// <summary>The tools around the scan: the word list, trying one track by hand and rescanning one track.</summary>
public class ToolsApiService : IService
{
	public object Get(WordListRequest request)
	{
		WordList list = WordList.Default();
		PluginConfiguration c = HallPassState.Config;
		string[] Tier(int weight) => list.Words.Where((KeyValuePair<string, int> p) => p.Value == weight).Select((KeyValuePair<string, int> p) => p.Key).OrderBy((string s) => s, StringComparer.Ordinal).ToArray();
		return new
		{
			Severe = Tier(WordList.SevereWeight),
			Moderate = Tier(WordList.ModerateWeight),
			Mild = Tier(WordList.MildWeight),
			Stems = list.Stems.Keys.OrderBy((string s) => s, StringComparer.Ordinal).ToArray(),
			Allow = list.Allow.OrderBy((string s) => s, StringComparer.Ordinal).ToArray(),
			Edits = new
			{
				Severe = WordList.Clean(c.ExtraSevereWords),
				Moderate = WordList.Clean(c.ExtraModerateWords),
				Mild = WordList.Clean(c.ExtraMildWords),
				Allow = WordList.Clean(c.ExtraAllowedWords),
				Ignored = WordList.Clean(c.IgnoredWords)
			}
		};
	}

	public object Post(LookupRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Title) && string.IsNullOrWhiteSpace(request.Isrc) && string.IsNullOrWhiteSpace(request.Upc))
		{
			throw new ArgumentException("Give at least a title, an ISRC or an album barcode.");
		}
		string? Digits(string? s) => string.IsNullOrWhiteSpace(s) ? null : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
		PluginConfiguration real = HallPassState.Config;
		// a hand-made copy so the sources are asked even while online lookups are off in the settings: the button press is the permission
		PluginConfiguration probe = new PluginConfiguration
		{
			OnlineEnabled = true,
			OnlineDeezer = true,
			OnlineITunes = true,
			OnlineLyrics = true,
			OnlineCacheDays = 0,
			OnlineNegativeDays = 0,
			MinWordsForClean = real.MinWordsForClean,
			PerTermCap = real.PerTermCap
		};
		TrackInfo info = new TrackInfo((request.Title ?? string.Empty).Trim(), (request.Artist ?? string.Empty).Trim(), string.IsNullOrWhiteSpace(request.Album) ? null : request.Album.Trim(), request.Seconds > 0 ? request.Seconds : null, Digits(request.Isrc));
		TrackIdentifiers ids = new TrackIdentifiers(info.Isrc, Digits(request.Upc));
		System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
		List<EvidenceItem> found = new List<EvidenceItem>();
		found.AddRange(OnlineStage.LookupAsync(info, ids, probe, cts.Token).GetAwaiter().GetResult());
		ProfanityScanner scanner = new ProfanityScanner(HallPassState.WordListFor(real), HallPassState.ScannerOptionsFor(real));
		EvidenceItem? lyrics = LyricsStage.LookupAsync(info, scanner, probe, cts.Token).GetAwaiter().GetResult();
		if (lyrics != null)
		{
			found.Add(lyrics);
		}
		HallPassState.SaveOnlineCache();
		DecisionResult decision = DecisionEngine.Decide(ManualOverride.None, found, HallPassState.DecisionSettingsFor(real));
		return new
		{
			Decision = decision.Decision.ToString(),
			decision.Confidence,
			decision.Reason,
			Evidence = found.Select((EvidenceItem e) => new { e.Source, Verdict = e.Verdict.ToString(), e.Confidence, e.Detail }).ToArray(),
			Note = found.Count == 0 ? "No source knew this track (or none could be reached)." : null
		};
	}

	public object Post(RescanRequest request)
	{
		if (!JobGate.TryEnter("rescan-one"))
		{
			throw new InvalidOperationException("Another job is running; try again when it has finished.");
		}
		try
		{
			if (Plugin.Instance!.LibraryManager.GetItemById(request.ItemId) is not MediaBrowser.Controller.Entities.Audio.Audio item)
			{
				throw new ArgumentException("That is not a track Emby knows.");
			}
			PluginConfiguration config = HallPassState.Config;
			ProfanityScanner scanner = new ProfanityScanner(HallPassState.WordListFor(config), HallPassState.ScannerOptionsFor(config));
			EvidenceStore store = HallPassState.Store;
			TrackRecord? record = TrackAnalyzer.AnalyzeAsync(item, store.Get(request.ItemId), config, scanner, HallPassState.EvidenceVersionFor(config), true, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
			if (record == null)
			{
				throw new InvalidOperationException("The track could not be looked at.");
			}
			store.Upsert(record, HallPassState.DecisionSettingsFor(config));
			HallPassState.Save();
			HallPassState.SaveOnlineCache();
			NotificationHost.ReviewBaseline();
			return HallPassApiService.Row(store.Get(request.ItemId)!);
		}
		finally
		{
			JobGate.Exit();
		}
	}

	public object Post(WordListTestRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Text))
		{
			throw new ArgumentException("Give some text to score.");
		}
		if (request.Text.Length > 20000)
		{
			throw new ArgumentException("At most 20,000 characters.");
		}
		WordList list = WordList.Default().WithEdits(request.ExtraSevereWords, request.ExtraModerateWords, request.ExtraMildWords, request.ExtraAllowedWords, request.IgnoredWords);
		ProfanityScanner scanner = new ProfanityScanner(list, HallPassState.ScannerOptionsFor(HallPassState.Config));
		ScanResult result = scanner.Scan(request.Text);
		EvidenceItem evidence = scanner.ToEvidence(result);
		return new
		{
			result.Score,
			result.WordCount,
			Hits = result.Hits.Select((WordHit h) => new { h.Term, h.Weight, h.Count }).ToArray(),
			Verdict = evidence.Verdict.ToString(),
			evidence.Confidence,
			evidence.Detail
		};
	}
}
