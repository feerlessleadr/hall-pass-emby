using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace HallPass.Services;

/// <summary>Phase 0/1 probe: a dry-run scan of the music library (decisions only, never lyrics, never writes). Removed before release.</summary>
[Authenticated(Roles = "Admin")]
[Route("/HallPass/Probe/Scan", "GET", Summary = "Dry run: decide every audio track from its lyrics and title markers. Writes nothing.")]
public class ProbeScanRequest : IReturn<object>
{
	public int Limit { get; set; } = 1000;
}

public class ProbeScanService : IService
{
	private static Plugin P => Plugin.Instance ?? throw new InvalidOperationException("Hall Pass is not started.");

	public async Task<object> Get(ProbeScanRequest request)
	{
		ProfanityScanner scanner = new ProfanityScanner(WordList.Default());
		BaseItem[] items = P.LibraryManager.GetItemList(new InternalItemsQuery
		{
			IncludeItemTypes = new[] { "Audio" },
			Recursive = true,
			Limit = Math.Clamp(request.Limit, 1, 5000)
		});

		List<object> rows = new List<object>();
		foreach (BaseItem item in items)
		{
			List<EvidenceItem> evidence = new List<EvidenceItem>();
			string lyricsNote = "none";
			ScanResult best = null;
			int streamsRead = 0;
			MediaSourceInfo source = P.MediaSourceManager.GetStaticMediaSources(item, true, false, null).FirstOrDefault();
			if (source != null)
			{
				foreach (MediaStream stream in source.MediaStreams.Where((MediaStream s) => s.Type == MediaStreamType.Subtitle))
				{
					try
					{
						string input = stream.IsExternal ? stream.Path : source.Path;
						string text = await P.SubtitleEncoder.GetSubtitles(input, source.Protocol, true, new Dictionary<string, string>(), stream, "txt", 0L, 0L, false, EncodingContext.Static, CancellationToken.None).ConfigureAwait(false);
						if (text != null && File.Exists(text))
						{
							text = File.ReadAllText(text);
						}
						ScanResult result = scanner.Scan(text ?? string.Empty);
						streamsRead++;
						if (best == null || result.Score > best.Score || (result.Score == best.Score && result.WordCount > best.WordCount))
						{
							best = result;
						}
					}
					catch (Exception)
					{
						// a stream that cannot be read is no evidence
					}
				}
			}
			if (best != null)
			{
				EvidenceItem lyrics = scanner.ToEvidence(best);
				evidence.Add(lyrics);
				lyricsNote = streamsRead + " stream(s), " + best.WordCount + " words";
			}
			string[] folders = item.Path == null ? new string[0] : Enumerable.Reverse((Path.GetDirectoryName(item.Path) ?? string.Empty).Split(Path.DirectorySeparatorChar)).Take(3).ToArray();
			Audio audio = item as Audio;
			evidence.AddRange(TitleHeuristics.Evaluate(item.Name, audio?.Album, folders));
			DecisionResult decision = DecisionEngine.Decide(ManualOverride.None, evidence, DecisionSettings.Balanced);
			rows.Add(new
			{
				Id = item.InternalId,
				item.Name,
				Artist = audio?.Artists != null ? string.Join(", ", audio.Artists) : null,
				Album = audio?.Album,
				Decision = decision.Decision.ToString(),
				ReviewReason = decision.ReviewReason.ToString(),
				decision.Confidence,
				decision.Reason,
				Lyrics = lyricsNote,
				Evidence = evidence.Select((EvidenceItem e) => e.Source + ":" + e.Verdict + ":" + e.Confidence.ToString("0.00") + (string.IsNullOrEmpty(e.Detail) ? string.Empty : " (" + e.Detail + ")")).ToArray()
			});
		}
		return rows;
	}
}
