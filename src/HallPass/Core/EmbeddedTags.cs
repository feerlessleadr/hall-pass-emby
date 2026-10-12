using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HallPass.Core;

/// <summary>What a catalog (or a file) says about a track: explicit, clean, or nothing definite.</summary>
public enum ExplicitFlag
{
	Unknown,
	Explicit,
	Clean,
}

/// <summary>Catalog identifiers read from a file's tags.</summary>
public sealed record TrackIdentifiers(string? Isrc, string? Upc);

/// <summary>
/// Stage 1: the explicit/clean flags and the catalog identifiers (ISRC, album barcode) inside a file's own tags. Tag names are lower case
/// (as <c>ffprobe</c> output is made lower case here). Pure logic: no files, no Emby types.
/// </summary>
public static class EmbeddedTags
{
	public const string Source = "embedded";

	public const double ExplicitConfidence = 1.0;

	public const double CleanConfidence = 0.9;

	/// <summary>A plain EXPLICIT=0 style field is less standard than the iTunes advisory, so it counts for less.</summary>
	public const double WeakCleanConfidence = 0.7;

	/// <summary>The embedded verdicts: at most one explicit and one clean item (the strongest of each).</summary>
	public static IReadOnlyList<EvidenceItem> Evaluate(IReadOnlyDictionary<string, string> tags)
	{
		EvidenceItem? explicitItem = null;
		EvidenceItem? cleanItem = null;
		void Note(EvidenceItem item)
		{
			if (item.Verdict == Verdict.Explicit && (explicitItem == null || item.Confidence > explicitItem.Confidence))
			{
				explicitItem = item;
			}
			else if (item.Verdict == Verdict.Clean && (cleanItem == null || item.Confidence > cleanItem.Confidence))
			{
				cleanItem = item;
			}
		}

		// The iTunes advisory (ID3 TXXX:ITUNESADVISORY, MP4 rtng): 1 or 4 explicit, 2 clean, 0 nothing.
		foreach (string key in new[] { "itunesadvisory", "rtng" })
		{
			if (tags.TryGetValue(key, out string? v) && ParseAdvisoryNumber(v) is { } n)
			{
				Note(FromAdvisoryNumber(n, key + "=" + v.Trim()));
			}
		}
		// Free-form fields: words and yes/no.
		foreach (string key in new[] { "explicit", "contentadvisory", "advisory", "parental_advisory", "parentaladvisory" })
		{
			if (!tags.TryGetValue(key, out string? v) || string.IsNullOrWhiteSpace(v))
			{
				continue;
			}
			string text = v.Trim().ToLowerInvariant();
			if (text is "1" or "true" or "yes" or "explicit" or "y" or "e" || text.StartsWith("explicit", StringComparison.Ordinal))
			{
				Note(new EvidenceItem(Source, Verdict.Explicit, ExplicitConfidence, key + "=" + v.Trim()));
			}
			else if (text is "2" or "clean" or "cleaned" or "edited" or "radio edit")
			{
				Note(new EvidenceItem(Source, Verdict.Clean, CleanConfidence, key + "=" + v.Trim()));
			}
			else if (text is "0" or "false" or "no" or "none" or "not explicit" or "notexplicit" or "n")
			{
				Note(new EvidenceItem(Source, Verdict.Clean, WeakCleanConfidence, key + "=" + v.Trim()));
			}
		}
		List<EvidenceItem> result = new List<EvidenceItem>();
		if (explicitItem != null)
		{
			result.Add(explicitItem);
		}
		if (cleanItem != null)
		{
			result.Add(cleanItem);
		}
		return result;
	}

	/// <summary>The evidence from an MP4 <c>rtng</c> atom value found inside the file itself (ffprobe does not always show it).</summary>
	public static EvidenceItem? FromRtngAtom(int? value)
	{
		return value is null or 0 ? null : FromAdvisoryNumber(value.Value, "rtng atom=" + value.Value);
	}

	private static int? ParseAdvisoryNumber(string? text)
	{
		return int.TryParse(text?.Trim(), out int n) ? n : null;
	}

	private static EvidenceItem FromAdvisoryNumber(int n, string detail)
	{
		return n switch
		{
			1 or 4 => new EvidenceItem(Source, Verdict.Explicit, ExplicitConfidence, detail),
			2 => new EvidenceItem(Source, Verdict.Clean, CleanConfidence, detail),
			_ => new EvidenceItem(Source, Verdict.None, 0, detail),
		};
	}

	/// <summary>The ISRC (from <c>isrc</c> or an MP3 <c>tsrc</c> frame) and the album barcode (<c>barcode</c>, <c>upc</c>, <c>ean</c>), when valid.</summary>
	public static TrackIdentifiers Identifiers(IReadOnlyDictionary<string, string> tags)
	{
		string? isrc = null;
		foreach (string key in new[] { "isrc", "tsrc" })
		{
			if (tags.TryGetValue(key, out string? v) && v != null)
			{
				// a tag can hold several, separated by ; / or a space: take the first valid one
				foreach (string part in v.Split(new[] { ';', '/', ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					string? clean = TrackMatcher.NormalizeIsrc(part);
					if (clean != null)
					{
						isrc = clean;
						break;
					}
				}
			}
			if (isrc != null)
			{
				break;
			}
		}
		string? upc = null;
		foreach (string key in new[] { "barcode", "upc", "ean" })
		{
			if (tags.TryGetValue(key, out string? v) && v != null)
			{
				string digits = new string(v.Where(char.IsDigit).ToArray());
				if (digits.Length is >= 8 and <= 14)
				{
					upc = digits;
					break;
				}
			}
		}
		return new TrackIdentifiers(isrc, upc);
	}

	/// <summary>Reads <c>ffprobe -of json -show_entries format_tags:stream_tags</c> output into one lower-case tag dictionary (the first value of a name wins).</summary>
	public static IReadOnlyDictionary<string, string> ParseProbeJson(string? json)
	{
		Dictionary<string, string> tags = new Dictionary<string, string>(StringComparer.Ordinal);
		if (string.IsNullOrWhiteSpace(json))
		{
			return tags;
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			void AddFrom(JsonElement holder)
			{
				if (holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("tags", out JsonElement t) && t.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty p in t.EnumerateObject())
					{
						string name = p.Name.ToLowerInvariant();
						if (p.Value.ValueKind == JsonValueKind.String && !tags.ContainsKey(name))
						{
							tags[name] = p.Value.GetString() ?? string.Empty;
						}
					}
				}
			}
			if (doc.RootElement.ValueKind == JsonValueKind.Object)
			{
				if (doc.RootElement.TryGetProperty("format", out JsonElement format))
				{
					AddFrom(format);
				}
				if (doc.RootElement.TryGetProperty("streams", out JsonElement streams) && streams.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement s in streams.EnumerateArray())
					{
						AddFrom(s);
					}
				}
			}
		}
		catch (JsonException)
		{
		}
		return tags;
	}

	/// <summary>
	/// Looks for the iTunes advisory atom inside bytes of an MP4/M4A file: 'rtng', then a 'data' atom whose last byte is the value.
	/// Returns null when there is none. (0 none, 1 or 4 explicit, 2 clean.)
	/// </summary>
	public static int? FindRtngAtom(ReadOnlySpan<byte> data)
	{
		ReadOnlySpan<byte> rtng = "rtng"u8;
		ReadOnlySpan<byte> dataTag = "data"u8;
		int from = 0;
		while (from < data.Length - 21)
		{
			int i = data.Slice(from).IndexOf(rtng);
			if (i < 0)
			{
				return null;
			}
			int at = from + i;
			// 'rtng' [size 4] 'data' [version and flags 4] [locale 4] [value 1]
			if (at + 21 <= data.Length && data.Slice(at + 8, 4).SequenceEqual(dataTag))
			{
				return data[at + 20];
			}
			from = at + 4;
		}
		return null;
	}
}
