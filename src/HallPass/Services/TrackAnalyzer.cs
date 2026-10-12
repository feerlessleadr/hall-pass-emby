using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace HallPass.Services;

/// <summary>Gathers the evidence about one audio track from Emby: its lyric streams and the markers in its names. Reads, never writes.</summary>
internal static class TrackAnalyzer
{
	/// <summary>
	/// Looks at one track. Returns null when it is unchanged since the last scan (same file key and evidence settings) and a rescan was not forced.
	/// The returned record has the evidence but no decision yet: the store works that out.
	/// </summary>
	public static async Task<TrackRecord?> AnalyzeAsync(BaseItem item, TrackRecord? existing, PluginConfiguration config, ProfanityScanner scanner, string evidenceVersion, bool force, CancellationToken token)
	{
		MediaSourceInfo? source = Plugin.Instance!.MediaSourceManager.GetStaticMediaSources(item, true, false, null).FirstOrDefault();
		List<MediaStream> lyricStreams = source == null
			? new List<MediaStream>()
			: source.MediaStreams.Where((MediaStream s) => s.Type == MediaStreamType.Subtitle).ToList();
		string fileKey = FileKeyFor(item, lyricStreams);
		if (!EvidenceStore.NeedsRescan(existing, fileKey, evidenceVersion, force))
		{
			return null;
		}

		Audio? audio = item as Audio;
		TrackRecord record = new TrackRecord
		{
			ItemId = item.InternalId,
			FileKey = fileKey,
			EvidenceVersion = evidenceVersion,
			Name = item.Name ?? string.Empty,
			Artist = audio?.Artists != null ? string.Join(", ", audio.Artists) : string.Empty,
			Album = audio?.Album ?? string.Empty,
			Path = item.Path ?? string.Empty,
			LastScannedUtc = DateTime.UtcNow,
			Skipped = IsSkipped(item, config),
		};
		if (record.Skipped)
		{
			return record;
		}

		ScanResult? best = null;
		foreach (MediaStream stream in lyricStreams)
		{
			token.ThrowIfCancellationRequested();
			string? text = await ReadLyricsAsync(source!, stream, token).ConfigureAwait(false);
			if (text == null)
			{
				continue;
			}
			ScanResult result = scanner.Scan(text);
			if (best == null || result.Score > best.Score || (result.Score == best.Score && result.WordCount > best.WordCount))
			{
				best = result;
			}
		}
		if (best != null)
		{
			record.Evidence.Add(scanner.ToEvidence(best));
		}
		string[] folders = string.IsNullOrEmpty(item.Path)
			? new string[0]
			: Enumerable.Reverse((Path.GetDirectoryName(item.Path) ?? string.Empty).Split(Path.DirectorySeparatorChar)).Take(3).ToArray();
		record.Evidence.AddRange(TitleHeuristics.Evaluate(item.Name, audio?.Album, folders));
		return record;
	}

	/// <summary>The text of one lyric stream, or null when it cannot be read. Emby's subtitle encoder returns the path of a converted file in its cache.</summary>
	private static async Task<string?> ReadLyricsAsync(MediaSourceInfo source, MediaStream stream, CancellationToken token)
	{
		try
		{
			string input = stream.IsExternal && !string.IsNullOrEmpty(stream.Path) ? stream.Path : source.Path;
			string? text = await Plugin.Instance!.SubtitleEncoder.GetSubtitles(input, source.Protocol, true, new Dictionary<string, string>(), stream, "txt", 0L, 0L, false, EncodingContext.Static, token).ConfigureAwait(false);
			if (text != null && File.Exists(text))
			{
				text = File.ReadAllText(text);
			}
			return text;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not read a lyric stream of " + source.Path + ": " + ex.GetType().Name + " " + ex.Message);
			return null;
		}
	}

	private static bool IsSkipped(BaseItem item, PluginConfiguration config)
	{
		string[] skip = config.SkipGenres ?? new string[0];
		return skip.Length > 0 && (item.Genres ?? new string[0]).Any((string g) => skip.Any((string s) => string.Equals(g.Trim(), s.Trim(), StringComparison.OrdinalIgnoreCase)));
	}

	/// <summary>What the track looks like now: the file (path, size, time) and its lyric streams (and the time of external lyric files).</summary>
	private static string FileKeyFor(BaseItem item, List<MediaStream> lyricStreams)
	{
		string file = string.Empty;
		try
		{
			if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
			{
				FileInfo info = new FileInfo(item.Path);
				file = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
			}
		}
		catch (IOException)
		{
		}
		string streams = string.Join(";", lyricStreams.Select((MediaStream s) => s.Index + ":" + s.Codec + ":" + s.IsExternal + ":" + ExternalStamp(s)));
		return EvidenceStore.Fingerprint(item.Path, file, streams);
	}

	private static string ExternalStamp(MediaStream stream)
	{
		try
		{
			return stream.IsExternal && !string.IsNullOrEmpty(stream.Path) && File.Exists(stream.Path) ? new FileInfo(stream.Path).LastWriteTimeUtc.Ticks.ToString() : string.Empty;
		}
		catch (IOException)
		{
			return string.Empty;
		}
	}
}
