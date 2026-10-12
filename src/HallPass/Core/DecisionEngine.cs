using System;
using System.Collections.Generic;
using System.Linq;

namespace HallPass.Core;

/// <summary>The thresholds of the decision. A missed explicit track is worse than a falsely flagged clean one, so the defaults lean careful.</summary>
public sealed class DecisionSettings
{
	/// <summary>Explicit evidence at or above this is enough to call a track explicit.</summary>
	public double AutoExplicit { get; init; } = 0.75;

	/// <summary>Clean evidence at or above this (and no explicit evidence) is enough to call a track clean.</summary>
	public double AutoClean { get; init; } = 0.9;

	/// <summary>When explicit and clean evidence disagree, explicit still wins from this confidence upwards.</summary>
	public double ConflictExplicitWins { get; init; } = 0.95;

	/// <summary>How many different sources must say "clean" before a track is called clean.</summary>
	public int MinCleanSources { get; init; } = 1;

	/// <summary>Two or more different sources saying "clean" (for example lyrics with no scoring words and an online catalog saying not explicit), the strongest at this confidence or more, and nothing explicit: the track is clean.</summary>
	public double CorroboratedClean { get; init; } = 0.8;

	/// <summary>Clean evidence from here up (and no explicit evidence) makes a track "likely clean": probably fine, not confirmed. Below it the track goes to review.</summary>
	public double LikelyClean { get; init; } = 0.5;

	public static DecisionSettings Balanced => new DecisionSettings();

	/// <summary>Lower bar for explicit, and clean needs two independent sources.</summary>
	public static DecisionSettings Conservative => new DecisionSettings { AutoExplicit = 0.6, MinCleanSources = 2, CorroboratedClean = 0.9 };

	/// <summary>Higher bar for explicit and a lower one for clean.</summary>
	public static DecisionSettings Relaxed => new DecisionSettings { AutoExplicit = 0.85, AutoClean = 0.8 };
}

/// <summary>Turns the evidence about one track into a decision. Pure logic: no Emby types, no IO.</summary>
public static class DecisionEngine
{
	public static DecisionResult Decide(ManualOverride manualOverride, IReadOnlyList<EvidenceItem> evidence, DecisionSettings settings, bool skipped = false)
	{
		if (manualOverride == ManualOverride.Explicit)
		{
			return new DecisionResult(Decision.Explicit, 1.0, ReviewReason.None, "manual override");
		}
		if (manualOverride == ManualOverride.Clean)
		{
			return new DecisionResult(Decision.Clean, 1.0, ReviewReason.None, "manual override");
		}
		if (skipped)
		{
			return new DecisionResult(Decision.Skipped, 1.0, ReviewReason.None, "skipped (genre or library)");
		}

		List<EvidenceItem> explicitEvidence = evidence.Where((EvidenceItem e) => e.Verdict == Verdict.Explicit && e.Confidence > 0).ToList();
		List<EvidenceItem> cleanEvidence = evidence.Where((EvidenceItem e) => e.Verdict == Verdict.Clean && e.Confidence > 0).ToList();
		double bestExplicit = explicitEvidence.Count == 0 ? 0 : explicitEvidence.Max((EvidenceItem e) => e.Confidence);
		double bestClean = cleanEvidence.Count == 0 ? 0 : cleanEvidence.Max((EvidenceItem e) => e.Confidence);
		int cleanSources = cleanEvidence.Select((EvidenceItem e) => e.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
		bool conflict = bestExplicit > 0 && bestClean > 0;

		if (bestExplicit == 0 && bestClean == 0)
		{
			return new DecisionResult(Decision.Review, 0, ReviewReason.NoEvidence, "no evidence either way");
		}
		if (bestExplicit >= settings.AutoExplicit)
		{
			if (conflict && bestExplicit < settings.ConflictExplicitWins)
			{
				return new DecisionResult(Decision.Review, bestExplicit, ReviewReason.Conflict, "explicit and clean evidence disagree");
			}
			return new DecisionResult(Decision.Explicit, bestExplicit, ReviewReason.None, "explicit evidence " + bestExplicit.ToString("0.00"));
		}
		bool corroborated = cleanSources >= 2 && bestClean >= settings.CorroboratedClean;
		if (bestExplicit == 0 && ((bestClean >= settings.AutoClean && cleanSources >= settings.MinCleanSources) || corroborated))
		{
			return new DecisionResult(Decision.Clean, bestClean, ReviewReason.None, "clean evidence " + bestClean.ToString("0.00"));
		}
		if (bestExplicit == 0 && bestClean >= settings.LikelyClean)
		{
			return new DecisionResult(Decision.LikelyClean, bestClean, ReviewReason.None, "clean-looking, not confirmed (" + bestClean.ToString("0.00") + ")");
		}
		if (bestExplicit == 0)
		{
			return new DecisionResult(Decision.Review, bestClean, ReviewReason.WeakClean, "only a faint sign of a clean track");
		}
		return new DecisionResult(Decision.Review, bestExplicit, conflict ? ReviewReason.Conflict : ReviewReason.Suspected, conflict ? "explicit and clean evidence disagree" : "some sign of explicit content, not enough to be sure");
	}
}
