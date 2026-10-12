using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HallPass.Core;

/// <summary>What the plugin knows about a track (or what an online catalog says about one).</summary>
public sealed record TrackInfo(string Title, string Artist, string? Album, double? DurationSeconds, string? Isrc);

public sealed record MatchResult(double Confidence, string Reason);

/// <summary>
/// Decides whether an online catalog entry is the same recording as a local track, strictly, so the explicit flag of another version
/// (the clean edit, a live take) is not attached to the wrong file. ISRC beats everything; otherwise title, main artist and length must agree.
/// Pure logic.
/// </summary>
public static class TrackMatcher
{
	public const double IsrcMatch = 0.95;

	public const double TitleArtistDurationMatch = 0.8;

	public const double AlbumBonus = 0.05;

	/// <summary>Everything short of title, artist and length agreeing stays at or below this.</summary>
	public const double FuzzyCap = 0.6;

	/// <summary>Same title and artist but another length: probably another version, so weak evidence at most.</summary>
	public const double OtherVersion = 0.4;

	private static readonly Regex Featuring = new Regex(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\b[^\)\]]*[\)\]]|\s[-–]?\s*(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Remaster = new Regex(@"[\(\[][^\)\]]*\b(?:remaster(?:ed)?|deluxe|bonus track|album version|single version|\d{4} version|mono|stereo)\b[^\)\]]*[\)\]]|\s[-–]\s*(?:\d{4}\s+)?(?:remaster(?:ed)?(?:\s+\d{4})?|album version|single version|mono|stereo)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex NonWord = new Regex(@"[^\p{L}\p{N}\s]", RegexOptions.Compiled);

	private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);

	/// <summary>Lower case, no accents, no "feat." part, no remaster or album-version suffix, no punctuation. "Radio edit", "clean", "explicit" and "live" stay: they name a different version.</summary>
	public static string NormalizeTitle(string title)
	{
		string s = Featuring.Replace(title ?? string.Empty, string.Empty);
		s = Remaster.Replace(s, string.Empty);
		return Squash(s);
	}

	/// <summary>The main artist, lower case, no accents, without "the" in front or a featured guest behind.</summary>
	public static string NormalizeArtist(string artist)
	{
		string s = Featuring.Replace(artist ?? string.Empty, string.Empty);
		s = s.Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
		s = Squash(s);
		return s.StartsWith("the ", StringComparison.Ordinal) ? s.Substring(4) : s;
	}

	/// <summary>1 for identical text, down to 0, from the edit distance.</summary>
	public static double Similarity(string a, string b)
	{
		if (a.Length == 0 && b.Length == 0)
		{
			return 1.0;
		}
		int distance = Distance(a, b);
		return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
	}

	public static MatchResult Match(TrackInfo local, TrackInfo candidate, double durationToleranceSeconds = 3.0)
	{
		string? isrcA = NormalizeIsrc(local.Isrc);
		string? isrcB = NormalizeIsrc(candidate.Isrc);
		if (isrcA != null && isrcA == isrcB)
		{
			return new MatchResult(IsrcMatch, "same ISRC");
		}

		string titleA = NormalizeTitle(local.Title);
		string titleB = NormalizeTitle(candidate.Title);
		string artistA = NormalizeArtist(local.Artist);
		string artistB = NormalizeArtist(candidate.Artist);
		bool sameTitle = titleA.Length > 0 && titleA == titleB;
		bool sameArtist = artistA.Length > 0 && artistA == artistB;
		bool durationsKnown = local.DurationSeconds.HasValue && candidate.DurationSeconds.HasValue;
		bool sameLength = durationsKnown && Math.Abs(local.DurationSeconds!.Value - candidate.DurationSeconds!.Value) <= durationToleranceSeconds;

		if (sameTitle && sameArtist && sameLength)
		{
			bool sameAlbum = local.Album != null && candidate.Album != null && Squash(local.Album).Length > 0 && Squash(local.Album) == Squash(candidate.Album);
			return new MatchResult(TitleArtistDurationMatch + (sameAlbum ? AlbumBonus : 0), sameAlbum ? "same title, artist, length and album" : "same title, artist and length");
		}
		if (sameTitle && sameArtist && durationsKnown)
		{
			return new MatchResult(OtherVersion, "same title and artist but another length (probably another version)");
		}
		double titleSimilarity = Similarity(titleA, titleB);
		double artistSimilarity = Similarity(artistA, artistB);
		if (titleSimilarity >= 0.9 && artistSimilarity >= 0.85 && titleA.Length > 0 && artistA.Length > 0)
		{
			return new MatchResult(Math.Min(FuzzyCap, 0.3 + 0.3 * Math.Min(titleSimilarity, artistSimilarity)), "similar title and artist, length unknown or not compared");
		}
		return new MatchResult(0, "no match");
	}

	private static string? NormalizeIsrc(string? isrc)
	{
		if (string.IsNullOrWhiteSpace(isrc))
		{
			return null;
		}
		string s = new string(isrc.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
		return s.Length == 12 ? s : null;
	}

	private static string Squash(string text)
	{
		string s = text.Replace("&", " and ").Normalize(NormalizationForm.FormD);
		StringBuilder sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
			{
				sb.Append(c);
			}
		}
		s = NonWord.Replace(sb.ToString().ToLowerInvariant().Replace('\'', ' '), " ");
		return Spaces.Replace(s, " ").Trim();
	}

	private static int Distance(string a, string b)
	{
		int[] previous = new int[b.Length + 1];
		int[] current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++)
		{
			previous[j] = j;
		}
		for (int i = 1; i <= a.Length; i++)
		{
			current[0] = i;
			for (int j = 1; j <= b.Length; j++)
			{
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}
}
