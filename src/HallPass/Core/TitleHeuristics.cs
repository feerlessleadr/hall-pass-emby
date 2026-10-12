using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace HallPass.Core;

/// <summary>
/// Evidence from words in a track's title, album and folder names: "(Explicit)", "(Clean)", "(Radio Edit)", "(NSFW)", "(SFW)".
/// Medium confidence only: a marker is a hint, never the final word. Pure logic.
/// </summary>
public static class TitleHeuristics
{
	/// <summary>Confidence of an explicit marker.</summary>
	public const double ExplicitMarker = 0.6;

	/// <summary>Confidence of a clean marker.</summary>
	public const double CleanMarker = 0.6;

	/// <summary>A radio edit is often, not always, the clean version.</summary>
	public const double RadioEditMarker = 0.4;

	private static readonly Regex NotExplicit = new Regex(@"\b(?:not|non)[\s\-]?explicit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex BracketedExplicit = new Regex(@"[\(\[][^\)\]]*\b(?:explicit|nsfw)\b[^\)\]]*[\)\]]|[\(\[]\s*e\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex BareNsfw = new Regex(@"\bnsfw\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex BracketedClean = new Regex(@"[\(\[][^\)\]]*\b(?:clean|clean version|censored|edited|family friendly|sfw)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex BareSfw = new Regex(@"\bsfw\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex RadioEdit = new Regex(@"[\(\[][^\)\]]*\bradio (?:edit|version|mix)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	/// <summary>Looks at the title, the album and the folder names above the file.</summary>
	public static IReadOnlyList<EvidenceItem> Evaluate(string? title, string? album, IEnumerable<string>? folderNames)
	{
		List<EvidenceItem> found = new List<EvidenceItem>();
		Look("title", title, found);
		Look("album", album, found);
		if (folderNames != null)
		{
			foreach (string folder in folderNames)
			{
				Look("folder", folder, found);
			}
		}
		return found;
	}

	private static void Look(string source, string? text, List<EvidenceItem> found)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		bool saysNotExplicit = NotExplicit.IsMatch(text);
		if (!saysNotExplicit && (BracketedExplicit.IsMatch(text) || BareNsfw.IsMatch(text)))
		{
			found.Add(new EvidenceItem(source, Verdict.Explicit, ExplicitMarker, "marker in the " + source + " name"));
		}
		if (saysNotExplicit || BracketedClean.IsMatch(text) || BareSfw.IsMatch(text))
		{
			found.Add(new EvidenceItem(source, Verdict.Clean, CleanMarker, "clean marker in the " + source + " name"));
		}
		else if (RadioEdit.IsMatch(text))
		{
			found.Add(new EvidenceItem(source, Verdict.Clean, RadioEditMarker, "radio edit in the " + source + " name"));
		}
	}
}
