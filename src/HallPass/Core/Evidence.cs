namespace HallPass.Core;

/// <summary>What one piece of evidence says about a track.</summary>
public enum Verdict
{
	None,
	Explicit,
	Clean,
}

/// <summary>The outcome for a track. Review is "a person should look" (see <see cref="ReviewReason"/>).</summary>
public enum Decision
{
	Unscanned,
	Skipped,
	Explicit,
	Clean,
	Review,
}

/// <summary>A decision made by hand in the review screen. Always wins and is never replaced by a scan.</summary>
public enum ManualOverride
{
	None,
	Explicit,
	Clean,
}

/// <summary>Why a track is in the review queue (the tag is the same; the queue can be sorted by risk).</summary>
public enum ReviewReason
{
	None,

	/// <summary>Nothing said anything about the track.</summary>
	NoEvidence,

	/// <summary>Some evidence of explicit content, below the automatic threshold.</summary>
	Suspected,

	/// <summary>Sources disagree and the explicit side is not certain.</summary>
	Conflict,

	/// <summary>Only weak or too few signs of a clean track.</summary>
	WeakClean,
}

/// <summary>One finding from one source (embedded flag, title marker, lyrics, online catalog, ...). Never holds lyrics text.</summary>
public sealed record EvidenceItem(string Source, Verdict Verdict, double Confidence, string Detail);

/// <summary>The result of <see cref="DecisionEngine.Decide"/>.</summary>
public sealed record DecisionResult(Decision Decision, double Confidence, ReviewReason ReviewReason, string Reason);
