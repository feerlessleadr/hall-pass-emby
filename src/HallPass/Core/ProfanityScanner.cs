using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HallPass.Core;

/// <summary>One word that counted: the list entry it matched (not the lyric text), its weight and how often it was heard.</summary>
public sealed record WordHit(string Term, int Weight, int Count);

/// <summary>What a scan found. Never holds the lyrics.</summary>
public sealed record ScanResult(int Score, int WordCount, IReadOnlyList<WordHit> Hits);

public sealed class ScannerOptions
{
	/// <summary>A weighted score from here up is explicit.</summary>
	public int ExplicitScore { get; init; } = 3;

	/// <summary>A weighted score from here up (but below <see cref="ExplicitScore"/>) is a suspect.</summary>
	public int SuspectScore { get; init; } = 1;

	public double ExplicitConfidence { get; init; } = 0.75;

	public double SuspectConfidence { get; init; } = 0.5;

	/// <summary>Lyrics with no hits only weakly support "clean", because lyrics can be incomplete or wrong.</summary>
	public double CleanConfidence { get; init; } = 0.5;

	/// <summary>Fewer words than this and "no hits" says nothing (an instrumental's placeholder, a few lines).</summary>
	public int MinWordsForClean { get; init; } = 15;

	/// <summary>The most times one term counts toward the score (a chorus that repeats one mild word); 0 means no limit.</summary>
	public int PerTermCap { get; init; } = 0;
}

/// <summary>
/// Looks for profanity in lyrics: whole words only (no "Scunthorpe" problem), a few inflections, masked and leet spellings (f*ck, sh!t, fuuuck),
/// and compounds through a short list of stems. Pure logic: no Emby types, no IO.
/// </summary>
public sealed class ProfanityScanner
{
	private static readonly Regex MetaLine = new Regex(@"^\s*\[(?:ar|ti|al|au|by|offset|length|re|ve|la|id|tool|#)[^\]]*\]\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex LineTime = new Regex(@"\[\s*\d{1,3}:\d{2}(?:[.:]\d{1,3})?\s*\]", RegexOptions.Compiled);

	private static readonly Regex WordTime = new Regex(@"<\s*\d{1,3}:\d{2}(?:[.:]\d{1,3})?\s*>", RegexOptions.Compiled);

	private static readonly Regex TokenPattern = new Regex(@"[\p{L}\p{N}'*@$!#]+", RegexOptions.Compiled);

	private static readonly string[] Suffixes = { "s", "es", "ed", "ing", "in", "y", "ie", "ies" };

	private static readonly Dictionary<char, char> Leet = new Dictionary<char, char>
	{
		['0'] = 'o', ['1'] = 'i', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['7'] = 't', ['@'] = 'a', ['$'] = 's', ['!'] = 'i',
	};

	private readonly WordList _list;

	private readonly ScannerOptions _options;

	public ProfanityScanner(WordList list, ScannerOptions? options = null)
	{
		_list = list;
		_options = options ?? new ScannerOptions();
	}

	/// <summary>Removes LRC timing marks and metadata lines (the lyrics' own text is untouched).</summary>
	public static string StripTimings(string text)
	{
		string result = MetaLine.Replace(text, string.Empty);
		result = LineTime.Replace(result, " ");
		return WordTime.Replace(result, " ");
	}

	public ScanResult Scan(string lyrics)
	{
		if (string.IsNullOrWhiteSpace(lyrics))
		{
			return new ScanResult(0, 0, Array.Empty<WordHit>());
		}
		string text = StripTimings(lyrics).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
		text = text.Replace('’', '\'').Replace('‘', '\'').Replace('´', '\'').Replace('`', '\'');

		int wordCount = 0;
		Dictionary<string, (int Weight, int Count)> hits = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
		foreach (Match m in TokenPattern.Matches(text))
		{
			string token = m.Value.Trim('\'', '!', '#');
			if (token.Length == 0 || !token.Any(char.IsLetterOrDigit))
			{
				continue;
			}
			if (token.Any(char.IsLetter))
			{
				wordCount++;
			}
			(string Term, int Weight)? hit = Match(token);
			if (hit.HasValue)
			{
				hits[hit.Value.Term] = hits.TryGetValue(hit.Value.Term, out (int Weight, int Count) old) ? (old.Weight, old.Count + 1) : (hit.Value.Weight, 1);
			}
		}

		List<WordHit> list = hits.Select((KeyValuePair<string, (int Weight, int Count)> kv) => new WordHit(kv.Key, kv.Value.Weight, kv.Value.Count))
			.OrderByDescending((WordHit h) => h.Weight * h.Count).ThenBy((WordHit h) => h.Term, StringComparer.Ordinal).ToList();
		int score = list.Sum((WordHit h) => h.Weight * (_options.PerTermCap > 0 ? Math.Min(h.Count, _options.PerTermCap) : h.Count));
		return new ScanResult(score, wordCount, list);
	}

	/// <summary>Turns a scan into evidence: explicit above the threshold, a suspect below it, a weak "clean" when long lyrics had no hits, nothing otherwise.</summary>
	public EvidenceItem ToEvidence(ScanResult result, string source = "lyrics")
	{
		string terms = string.Join(", ", result.Hits.Select((WordHit h) => h.Count > 1 ? h.Term + " x" + h.Count : h.Term));
		if (result.Score >= _options.ExplicitScore)
		{
			return new EvidenceItem(source, Verdict.Explicit, _options.ExplicitConfidence, "score " + result.Score + ": " + terms);
		}
		if (result.Score >= _options.SuspectScore)
		{
			return new EvidenceItem(source, Verdict.Explicit, _options.SuspectConfidence, "suspect, score " + result.Score + ": " + terms);
		}
		if (result.WordCount >= _options.MinWordsForClean)
		{
			string mild = result.Hits.Count == 0 ? string.Empty : " (mild words, not scored: " + terms + ")";
			return new EvidenceItem(source, Verdict.Clean, _options.CleanConfidence, "no scoring hits in " + result.WordCount + " words" + mild);
		}
		return new EvidenceItem(source, Verdict.None, 0, "too little text to say");
	}

	// ----- matching one token ----------------------------------------------------------------------------------

	private (string Term, int Weight)? Match(string token)
	{
		if (_list.Allow.Contains(token))
		{
			return null;
		}
		(string Term, int Weight)? best = null;
		foreach (string candidate in Candidates(token))
		{
			if (_list.Allow.Contains(candidate))
			{
				continue;
			}
			(string Term, int Weight)? hit = MatchCandidate(candidate);
			if (hit.HasValue && (!best.HasValue || hit.Value.Weight > best.Value.Weight))
			{
				best = hit;
			}
		}
		return best;
	}

	/// <summary>The token as written, with leet symbols turned into letters, and with long runs of one letter squeezed (fuuuck).</summary>
	private static IEnumerable<string> Candidates(string token)
	{
		HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
		List<string> bases = new List<string> { token };
		if (token.Length >= 3 && token.Any(char.IsLetter) && token.Any((char c) => Leet.ContainsKey(c)))
		{
			StringBuilder sb = new StringBuilder(token.Length);
			foreach (char c in token)
			{
				sb.Append(Leet.TryGetValue(c, out char mapped) ? mapped : c);
			}
			string mappedToken = sb.ToString();
			if (mappedToken.All((char c) => char.IsLetter(c) || c == '*' || c == '\x27'))
			{
				bases.Add(mappedToken);
			}
		}
		foreach (string b in bases)
		{
			if (seen.Add(b))
			{
				yield return b;
			}
			string squeezedToTwo = SqueezeRuns(b, 2);
			if (seen.Add(squeezedToTwo))
			{
				yield return squeezedToTwo;
			}
			string squeezedToOne = SqueezeRuns(b, 1);
			if (squeezedToOne != b && seen.Add(squeezedToOne) && HasLongRun(b))
			{
				yield return squeezedToOne;
			}
		}
	}

	private static bool HasLongRun(string s)
	{
		for (int i = 2; i < s.Length; i++)
		{
			if (s[i] == s[i - 1] && s[i] == s[i - 2] && char.IsLetter(s[i]))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>Runs of three or more of the same letter become <paramref name="keep"/> letters (keep 2 for "asssss", 1 for "fuuuuck").</summary>
	private static string SqueezeRuns(string s, int keep)
	{
		if (!HasLongRun(s))
		{
			return s;
		}
		StringBuilder sb = new StringBuilder(s.Length);
		int run = 0;
		for (int i = 0; i < s.Length; i++)
		{
			run = i > 0 && s[i] == s[i - 1] ? run + 1 : 1;
			if (run <= keep || !char.IsLetter(s[i]))
			{
				sb.Append(s[i]);
			}
		}
		return sb.ToString();
	}

	private (string Term, int Weight)? MatchCandidate(string c)
	{
		if (c.EndsWith("'s", StringComparison.Ordinal))
		{
			c = c.Substring(0, c.Length - 2);
		}
		if (c.Length == 0)
		{
			return null;
		}
		(string Term, int Weight)? exact = MatchBase(c);
		if (exact.HasValue)
		{
			return exact;
		}
		foreach (string suffix in Suffixes)
		{
			if (c.Length > suffix.Length + 1 && c.EndsWith(suffix, StringComparison.Ordinal))
			{
				string stem = c.Substring(0, c.Length - suffix.Length);
				(string Term, int Weight)? viaStem = MatchBase(stem);
				if (viaStem.HasValue)
				{
					return viaStem;
				}
				if (stem.Length >= 3 && stem[stem.Length - 1] == stem[stem.Length - 2])
				{
					(string Term, int Weight)? doubled = MatchBase(stem.Substring(0, stem.Length - 1));
					if (doubled.HasValue)
					{
						return doubled;
					}
				}
			}
		}
		foreach (KeyValuePair<string, int> stem in _list.Stems)
		{
			if (c.Contains(stem.Key, StringComparison.Ordinal))
			{
				return (stem.Key, stem.Value);
			}
		}
		return null;
	}

	/// <summary>A list word, or a masked spelling of one (a '*' stands for any one letter; at least two letters must be visible).</summary>
	private (string Term, int Weight)? MatchBase(string b)
	{
		if (_list.Words.TryGetValue(b, out int weight))
		{
			return (b, weight);
		}
		if (b.IndexOf('*') >= 0 && b.Count((char ch) => ch != '*') >= 2)
		{
			foreach (KeyValuePair<string, int> word in _list.Words)
			{
				if (word.Key.Length != b.Length)
				{
					continue;
				}
				bool same = true;
				for (int i = 0; i < b.Length && same; i++)
				{
					same = b[i] == '*' || b[i] == word.Key[i];
				}
				if (same)
				{
					return (word.Key, word.Value);
				}
			}
		}
		return null;
	}
}
