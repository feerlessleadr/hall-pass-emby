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

	public WordList(IDictionary<string, int> words, IDictionary<string, int> stems, IEnumerable<string> allow)
	{
		Words = new Dictionary<string, int>(words, StringComparer.Ordinal);
		Stems = new Dictionary<string, int>(stems, StringComparer.Ordinal);
		Allow = new HashSet<string>(allow, StringComparer.Ordinal);
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
		return new WordList(words, stems, allow);
	}

	/// <summary>The list that ships inside the plugin (embedded resource <c>HallPass.default-wordlist.json</c>).</summary>
	public static WordList Default()
	{
		Assembly assembly = typeof(WordList).Assembly;
		using Stream? stream = assembly.GetManifestResourceStream("HallPass.default-wordlist.json");
		if (stream == null)
		{
			throw new InvalidOperationException("The default word list is missing from the plugin.");
		}
		using StreamReader reader = new StreamReader(stream);
		return Parse(reader.ReadToEnd());
	}
}
