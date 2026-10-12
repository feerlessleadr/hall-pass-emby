using MediaBrowser.Model.Plugins;

namespace HallPass;

/// <summary>Saved settings (HallPass.xml).</summary>
public class PluginConfiguration : BasePluginConfiguration
{
	/// <summary>"Balanced", "Conservative" (lower bar for explicit, clean needs two sources) or "Relaxed".</summary>
	public string Preset { get; set; } = "Balanced";

	/// <summary>Clean-looking evidence from this confidence up (and nothing explicit) makes a track "likely clean".</summary>
	public double LikelyCleanConfidence { get; set; } = 0.5;

	/// <summary>Lyrics with fewer words than this cannot support "clean" (an instrumental's placeholder).</summary>
	public int MinWordsForClean { get; set; } = 15;

	/// <summary>The most times one word counts toward the lyrics score; 0 means no limit.</summary>
	public int PerTermCap { get; set; }

	/// <summary>Tracks in these genres are left alone (instrumental music has no lyrics to judge).</summary>
	public string[] SkipGenres { get; set; } = new string[] { "Classical", "Instrumental", "Soundtrack", "Score" };

	/// <summary>The four tags Hall Pass manages. A track gets exactly one of them (when writing is switched on).</summary>
	public string ExplicitTag { get; set; } = "explicit";

	public string CleanTag { get; set; } = "clean";

	public string LikelyCleanTag { get; set; } = "likely-clean";

	public string ReviewTag { get; set; } = "explicit-review";

	/// <summary>While true, scans only record decisions and no tag is written. (Tag writing is not built yet.)</summary>
	public bool DryRun { get; set; } = true;
}
