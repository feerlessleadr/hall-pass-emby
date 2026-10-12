using System;
using System.Collections.Generic;
using System.Linq;

namespace HallPass.Core;

/// <summary>The four tag names Hall Pass manages. A track carries at most one of them.</summary>
public sealed record TagNames(string Explicit, string Clean, string LikelyClean, string Review)
{
	public IReadOnlyList<string> All => new[] { Explicit, Clean, LikelyClean, Review };

	public bool IsManaged(string tag) => All.Any((string t) => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

	/// <summary>The tag a decision stands for, or null for no managed tag. Skipped tracks get none (or "clean" when asked), unscanned ones are never touched.</summary>
	public string? For(Decision decision, bool tagSkippedAsClean)
	{
		return decision switch
		{
			Decision.Explicit => Explicit,
			Decision.Clean => Clean,
			Decision.LikelyClean => LikelyClean,
			Decision.Review => Review,
			Decision.Skipped => tagSkippedAsClean ? Clean : null,
			_ => null,
		};
	}
}

/// <summary>What a track's tags are now and what they would be.</summary>
public sealed record TagChange(string[] Before, string[] After, bool Changed);

/// <summary>Works out the tags a track should carry. Exactly one managed tag per decided track, every other tag left alone and in its place. Pure logic.</summary>
public static class TagPlan
{
	/// <summary>
	/// The tags after applying <paramref name="decision"/>: all four managed tags are taken off and the one that fits is put on at the end.
	/// Unchanged (and flagged so) when the track already carries exactly that one managed tag, in any letter case, or when it was never scanned.
	/// </summary>
	public static TagChange ForDecision(IReadOnlyList<string> current, TagNames names, Decision decision, bool tagSkippedAsClean)
	{
		string[] before = current.ToArray();
		if (decision == Decision.Unscanned)
		{
			return new TagChange(before, before, false);
		}
		string? target = names.For(decision, tagSkippedAsClean);
		List<string> managedNow = before.Where(names.IsManaged).ToList();
		bool alreadyRight = target == null
			? managedNow.Count == 0
			: managedNow.Count == 1 && string.Equals(managedNow[0], target, StringComparison.OrdinalIgnoreCase);
		if (alreadyRight)
		{
			return new TagChange(before, before, false);
		}
		List<string> after = before.Where((string t) => !names.IsManaged(t)).ToList();
		if (target != null)
		{
			after.Add(target);
		}
		return new TagChange(before, after.ToArray(), true);
	}
}
