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

	/// <summary>While true, nothing is written to the library: scans only record decisions and "Apply" is refused (a preview of the changes is always available). The owner switches this off.</summary>
	public bool DryRun { get; set; } = true;

	/// <summary>Also tag tracks in skipped genres as clean (off: they get no tag).</summary>
	public bool TagSkippedAsClean { get; set; }

	/// <summary>Let Emby's metadata savers (NFO and so on) run after a tag change, like an edit in the dashboard. Off: only Emby's database changes.</summary>
	public bool RunMetadataSavers { get; set; } = true;

	/// <summary>How many tracks are saved at a time, and the pause between batches in milliseconds (keeps the server responsive).</summary>
	public int BatchSize { get; set; } = 200;

	public int BatchDelayMs { get; set; } = 50;

	/// <summary>Read each file's own tags with Emby's ffprobe: the advisory flag (when a file has one) and the ISRC and album barcode used for online lookups.</summary>
	public bool ReadFileTags { get; set; } = true;

	/// <summary>Online lookups (opt-in): ask Deezer and iTunes whether a track is explicit. Sends only artist, title, ISRC or album barcode. Off keeps everything on this server.</summary>
	public bool OnlineEnabled { get; set; }

	public bool OnlineDeezer { get; set; } = true;

	public bool OnlineITunes { get; set; } = true;

	/// <summary>How long an online answer is kept, in days (and a "nothing found" answer).</summary>
	public int OnlineCacheDays { get; set; } = 90;

	public int OnlineNegativeDays { get; set; } = 14;

	/// <summary>How many finished jobs the journal keeps (and anything younger than the days).</summary>
	public int JournalKeepJobs { get; set; } = 25;

	public int JournalKeepDays { get; set; } = 30;
}
