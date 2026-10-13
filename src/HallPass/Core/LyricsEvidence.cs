using System;
using System.Collections.Generic;
using System.Linq;

namespace HallPass.Core;

/// <summary>
/// Turns the lyrics of one track into evidence. A track can have several sources (the lyrics embedded in the audio file, a .lrc file next to it) and
/// they do not always agree: each is scanned on its own and gives its own evidence, so one source that sounds explicit and another that sounds clean
/// make the track a conflict for a person to look at, instead of the higher score quietly winning. Sources that give the same answer are shown as one line.
/// Pure logic.
/// </summary>
public static class LyricsEvidence
{
	/// <summary>The evidence for the scanned sources, each labelled with where its text came from (for the person reading it). Sources with too little text say nothing.</summary>
	public static IReadOnlyList<EvidenceItem> Combine(IEnumerable<(string Label, ScanResult Result)> sources, ProfanityScanner scanner)
	{
		List<(string Label, EvidenceItem Item)> items = sources
			.Select(((string Label, ScanResult Result) s) => (s.Label, Item: scanner.ToEvidence(s.Result)))
			.ToList();
		List<(string Label, EvidenceItem Item)> useful = items.Where(((string Label, EvidenceItem Item) i) => i.Item.Verdict != Verdict.None).ToList();
		if (useful.Count == 0)
		{
			// nothing usable: keep the first note ("too little text to say") so the review list can explain itself
			return items.Count == 0 ? Array.Empty<EvidenceItem>() : new[] { items[0].Item with { Detail = "from " + items[0].Label + ": " + items[0].Item.Detail } };
		}
		return useful
			.GroupBy(((string Label, EvidenceItem Item) i) => (i.Item.Verdict, i.Item.Confidence, i.Item.Detail))
			.Select((IGrouping<(Verdict Verdict, double Confidence, string Detail), (string Label, EvidenceItem Item)> g) =>
				g.First().Item with { Detail = "from " + string.Join(" and ", g.Select(((string Label, EvidenceItem Item) x) => x.Label).Distinct()) + ": " + g.Key.Detail })
			.ToList();
	}
}
