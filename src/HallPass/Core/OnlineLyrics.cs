using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HallPass.Core;

/// <summary>What the lyrics catalog (LRCLIB) said about one track. The text is scanned in memory and thrown away: never stored.</summary>
public sealed record LyricsAnswer(bool Found, bool Instrumental, string? Text, double? DurationSeconds, string? Message = null);

/// <summary>Reads LRCLIB's <c>/api/get</c> answer and prepares its text for the scanner. Pure logic.</summary>
public static class OnlineLyrics
{
	public const string Source = "lyrics-online";

	/// <summary>Online lyrics are matched by name, so they are weaker evidence than the file's own lyrics: neither verdict goes above this.</summary>
	public const double ConfidenceCap = 0.7;

	private static readonly Regex Timestamp = new Regex(@"\[\d{1,3}:\d{2}(?:[.:]\d{1,3})?\]|<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

	private static readonly Regex Tag = new Regex(@"^\[[a-zA-Z]{1,12}:[^\]]*\]\s*$", RegexOptions.Compiled | RegexOptions.Multiline);

	/// <summary>An HTTP 404 from LRCLIB means "no such track": pass the body (or null) here for 200 answers and call <see cref="NotFound"/> for 404.</summary>
	public static LyricsAnswer NotFound => new LyricsAnswer(false, false, null, null);

	public static LyricsAnswer Parse(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new LyricsAnswer(false, false, null, null, "empty answer");
		}
		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			JsonElement root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return new LyricsAnswer(false, false, null, null, "unexpected answer");
			}
			if (root.TryGetProperty("statusCode", out JsonElement code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 404)
			{
				return NotFound;
			}
			bool instrumental = root.TryGetProperty("instrumental", out JsonElement inst) && inst.ValueKind == JsonValueKind.True;
			string? plain = Str(root, "plainLyrics");
			string? synced = Str(root, "syncedLyrics");
			string? text = !string.IsNullOrWhiteSpace(plain) ? plain : (!string.IsNullOrWhiteSpace(synced) ? StripTimestamps(synced) : null);
			double? duration = root.TryGetProperty("duration", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
			if (instrumental || string.IsNullOrWhiteSpace(text))
			{
				return new LyricsAnswer(false, instrumental, null, duration);
			}
			return new LyricsAnswer(true, false, text, duration);
		}
		catch (JsonException)
		{
			return new LyricsAnswer(false, false, null, null, "answer was not JSON");
		}
	}

	/// <summary>Removes the timing marks of synced (.lrc style) lyrics and any [ar:...] header lines.</summary>
	public static string StripTimestamps(string synced)
	{
		string text = Tag.Replace(synced, string.Empty);
		return Timestamp.Replace(text, string.Empty).Trim();
	}

	/// <summary>Same verdict, confidence limited to <see cref="ConfidenceCap"/>, source and detail marked as online.</summary>
	public static EvidenceItem Capped(EvidenceItem item)
	{
		return new EvidenceItem(Source, item.Verdict, Math.Min(item.Confidence, ConfidenceCap), "from the lyrics catalog, " + item.Detail);
	}

	/// <summary>Whether the lengths are close enough to trust a catalog's lyrics for this track (the local length unknown counts as a match; a catalog length that is unknown does too).</summary>
	public static bool LengthsAgree(double? localSeconds, double? catalogSeconds, double toleranceSeconds = 5)
	{
		if (!localSeconds.HasValue || !catalogSeconds.HasValue)
		{
			return true;
		}
		return Math.Abs(localSeconds.Value - catalogSeconds.Value) <= toleranceSeconds;
	}

	private static string? Str(JsonElement root, string name)
	{
		return root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
	}
}
