using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace HallPass.Core;

/// <summary>
/// The words the lyrics scan looks for, in tiers (severe, moderate, mild), plus stems that count inside longer words (for compounds),
/// and an allowlist of words that must never count. All lower case. JSON shape:
/// {"severe":[...],"moderate":[...],"mild":[...],"stems":{"fuck":3,"shit":2},"allow":[...]}
/// </summary>
public sealed class WordList
{
	public const int SevereWeight = 3;

	public const int ModerateWeight = 2;

	/// <summary>Mild words are recorded but score nothing (the owner does not mind "damn").</summary>
	public const int MildWeight = 0;

	/// <summary>Whole words and their weight.</summary>
	public IReadOnlyDictionary<string, int> Words { get; }

	/// <summary>Strings that count when they appear inside a longer word (kept few and distinctive: "scunthorpe" must not match).</summary>
	public IReadOnlyDictionary<string, int> Stems { get; }

	/// <summary>Words that never count.</summary>
	public IReadOnlySet<string> Allow { get; }

	/// <summary>Words that count only as written (and with 's): "cocked" and "cocking" are not "cock", "dickens" is not "dick".</summary>
	public IReadOnlySet<string> ExactOnly { get; }

	/// <summary>Words that are also first names: written with a capital in the middle of a line ("Moby Dick", "Dick Tracy") they are a name and score nothing.</summary>
	public IReadOnlySet<string> NameLike { get; }

	/// <summary>Words with an everyday meaning too: heard once they are only a suspect (the track goes to review), heard twice or more they count in full.</summary>
	public IReadOnlySet<string> Ambiguous { get; }

	public WordList(IDictionary<string, int> words, IDictionary<string, int> stems, IEnumerable<string> allow, IEnumerable<string>? exactOnly = null, IEnumerable<string>? nameLike = null, IEnumerable<string>? ambiguous = null)
	{
		Words = new Dictionary<string, int>(words, StringComparer.Ordinal);
		Stems = new Dictionary<string, int>(stems, StringComparer.Ordinal);
		Allow = new HashSet<string>(allow, StringComparer.Ordinal);
		ExactOnly = new HashSet<string>(exactOnly ?? Array.Empty<string>(), StringComparer.Ordinal);
		NameLike = new HashSet<string>(nameLike ?? Array.Empty<string>(), StringComparer.Ordinal);
		Ambiguous = new HashSet<string>(ambiguous ?? Array.Empty<string>(), StringComparer.Ordinal);
	}

	public static WordList Parse(string json)
	{
		using JsonDocument doc = JsonDocument.Parse(json);
		JsonElement root = doc.RootElement;
		Dictionary<string, int> words = new Dictionary<string, int>(StringComparer.Ordinal);
		void AddTier(string name, int weight)
		{
			if (root.TryGetProperty(name, out JsonElement list) && list.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement w in list.EnumerateArray())
				{
					string? text = w.GetString()?.Trim().ToLowerInvariant();
					if (!string.IsNullOrEmpty(text))
					{
						// A word listed in two tiers counts at the higher weight.
						words[text] = words.TryGetValue(text, out int old) ? Math.Max(old, weight) : weight;
					}
				}
			}
		}
		AddTier("mild", MildWeight);
		AddTier("moderate", ModerateWeight);
		AddTier("severe", SevereWeight);
		Dictionary<string, int> stems = new Dictionary<string, int>(StringComparer.Ordinal);
		if (root.TryGetProperty("stems", out JsonElement stemObj) && stemObj.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty p in stemObj.EnumerateObject())
			{
				stems[p.Name.Trim().ToLowerInvariant()] = Math.Clamp(p.Value.GetInt32(), 1, SevereWeight);
			}
		}
		List<string> allow = new List<string>();
		if (root.TryGetProperty("allow", out JsonElement allowList) && allowList.ValueKind == JsonValueKind.Array)
		{
			allow.AddRange(allowList.EnumerateArray().Select((JsonElement e) => e.GetString()?.Trim().ToLowerInvariant() ?? string.Empty).Where((string s) => s.Length > 0));
		}
		List<string> Read(string name)
		{
			List<string> list = new List<string>();
			if (root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Array)
			{
				list.AddRange(e.EnumerateArray().Select((JsonElement x) => x.GetString()?.Trim().ToLowerInvariant() ?? string.Empty).Where((string t) => t.Length > 0));
			}
			return list;
		}
		return new WordList(words, stems, allow, Read("exactOnly"), Read("nameLike"), Read("ambiguous"));
	}

	/// <summary>The text of the list that ships inside the plugin (embedded resource <c>HallPass.default-wordlist.json</c>).</summary>
	public static string DefaultJson()
	{
		Assembly assembly = typeof(WordList).Assembly;
		using Stream? stream = assembly.GetManifestResourceStream("HallPass.default-wordlist.json");
		if (stream == null)
		{
			throw new InvalidOperationException("The default word list is missing from the plugin.");
		}
		using StreamReader reader = new StreamReader(stream);
		return reader.ReadToEnd();
	}

	public static WordList Default() => Parse(DefaultJson());

	/// <summary>
	/// This list changed by the owner: words put in a tier (a word moved to another tier takes the new one), words ignored (never score, even inside a stem match),
	/// and extra allowed words. An ignored word wins over everything else. Entries are trimmed and lower-cased; blanks and over-long entries are dropped.
	/// </summary>
	public WordList WithEdits(IEnumerable<string>? severe, IEnumerable<string>? moderate, IEnumerable<string>? mild, IEnumerable<string>? allow, IEnumerable<string>? ignore)
	{
		Dictionary<string, int> words = new Dictionary<string, int>(Words, StringComparer.Ordinal);
		void Put(IEnumerable<string>? list, int weight)
		{
			foreach (string w in Clean(list))
			{
				words[w] = weight;
			}
		}
		Put(mild, MildWeight);
		Put(moderate, ModerateWeight);
		Put(severe, SevereWeight);
		HashSet<string> ignored = new HashSet<string>(Clean(ignore), StringComparer.Ordinal);
		foreach (string w in ignored)
		{
			words.Remove(w);
		}
		Dictionary<string, int> stems = Stems.Where((KeyValuePair<string, int> p) => !ignored.Contains(p.Key)).ToDictionary((KeyValuePair<string, int> p) => p.Key, (KeyValuePair<string, int> p) => p.Value, StringComparer.Ordinal);
		HashSet<string> allowed = new HashSet<string>(Allow, StringComparer.Ordinal);
		allowed.UnionWith(Clean(allow));
		allowed.UnionWith(ignored);
		return new WordList(words, stems, allowed, ExactOnly, NameLike, Ambiguous);
	}

	public static IReadOnlyList<string> Clean(IEnumerable<string>? list)
	{
		return (list ?? Array.Empty<string>()).Select((string s) => (s ?? string.Empty).Trim().ToLowerInvariant()).Where((string s) => s.Length > 0 && s.Length <= 40).Distinct(StringComparer.Ordinal).ToList();
	}
}
