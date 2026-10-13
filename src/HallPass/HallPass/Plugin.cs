using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace HallPass;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IServerEntryPoint
{
	private readonly ILogger _logger;

	public static Plugin? Instance { get; private set; }

	public IApplicationPaths AppPaths { get; }

	public ILibraryManager LibraryManager { get; }

	public IMediaSourceManager MediaSourceManager { get; }

	public ISubtitleEncoder SubtitleEncoder { get; }

	public IFfmpegManager FfmpegManager { get; }

	public IUserManager UserManager { get; }

	public IUserDataManager UserDataManager { get; }

	public override string Name => "Hall Pass";

	public override string Description => "Finds explicit music and tags it, so a smart playlist can leave it out.";

	// Never changes: Emby identifies the plugin by this id.
	public override Guid Id => Guid.Parse("6ca80343-2b60-40de-ab81-7a41628fcaf2");

	public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ISubtitleEncoder subtitleEncoder, IFfmpegManager ffmpegManager, IUserManager userManager, IUserDataManager userDataManager, ILogManager logManager)
		: base(appPaths, xmlSerializer)
	{
		Instance = this;
		AppPaths = appPaths;
		LibraryManager = libraryManager;
		MediaSourceManager = mediaSourceManager;
		SubtitleEncoder = subtitleEncoder;
		FfmpegManager = ffmpegManager;
		UserManager = userManager;
		UserDataManager = userDataManager;
		_logger = logManager.GetLogger("HallPass");
	}

	/// <summary>Called when the server has started: begins watching for new tracks.</summary>
	public void Run()
	{
		AutoScan.Start();
	}

	public void Dispose()
	{
		AutoScan.Stop();
	}

	/// <summary>Writes a line to Emby's log.</summary>
	public static void Log(string message)
	{
		Instance?._logger.Info("[Hall Pass] " + message, Array.Empty<object>());
	}

	public override void UpdateConfiguration(BasePluginConfiguration configuration)
	{
		if (configuration is PluginConfiguration c)
		{
			string? problem = Validate(c);
			if (problem != null)
			{
				throw new ArgumentException(problem);
			}
		}
		base.UpdateConfiguration(configuration);
		if (configuration is PluginConfiguration)
		{
			// The thresholds may have changed: every decision is worked out again from the stored evidence (no rescan needed).
			HallPassState.RedecideAll();
		}
	}

	/// <summary>Checks and tidies the settings. Returns a message for a value that cannot be accepted.</summary>
	internal static string? Validate(PluginConfiguration c)
	{
		if (c.Preset != "Balanced" && c.Preset != "Conservative" && c.Preset != "Relaxed")
		{
			return "Preset must be Balanced, Conservative or Relaxed.";
		}
		if (c.LikelyCleanConfidence < 0.1 || c.LikelyCleanConfidence > 0.9)
		{
			return "The likely-clean confidence must be between 0.1 and 0.9.";
		}
		if (c.MinWordsForClean < 0 || c.MinWordsForClean > 500)
		{
			return "The minimum number of words must be between 0 and 500.";
		}
		if (c.PerTermCap < 0 || c.PerTermCap > 50)
		{
			return "The per-word cap must be between 0 and 50.";
		}
		if (c.BatchSize < 1 || c.BatchSize > 2000)
		{
			return "The batch size must be between 1 and 2000.";
		}
		if (c.BatchDelayMs < 0 || c.BatchDelayMs > 5000)
		{
			return "The pause between batches must be between 0 and 5000 milliseconds.";
		}
		if (c.OnlineCacheDays < 1 || c.OnlineCacheDays > 365 || c.OnlineNegativeDays < 1 || c.OnlineNegativeDays > 365)
		{
			return "Online answers are kept between 1 and 365 days.";
		}
		if (c.JournalKeepJobs < 1 || c.JournalKeepJobs > 500 || c.JournalKeepDays < 0 || c.JournalKeepDays > 3650)
		{
			return "The journal keeps between 1 and 500 jobs and 0 to 3650 days.";
		}
		c.ExplicitTag = (c.ExplicitTag ?? string.Empty).Trim();
		c.CleanTag = (c.CleanTag ?? string.Empty).Trim();
		c.LikelyCleanTag = (c.LikelyCleanTag ?? string.Empty).Trim();
		c.ReviewTag = (c.ReviewTag ?? string.Empty).Trim();
		string[] tags = { c.ExplicitTag, c.CleanTag, c.LikelyCleanTag, c.ReviewTag };
		if (tags.Any((string t) => t.Length == 0 || t.Length > 100))
		{
			return "Every tag name must be filled in (at most 100 characters).";
		}
		if (tags.Select((string t) => t.ToLowerInvariant()).Distinct().Count() != 4)
		{
			return "The four tag names must all be different.";
		}
		c.SkipGenres = (c.SkipGenres ?? new string[0]).Select((string g) => g.Trim()).Where((string g) => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		return null;
	}

	public IEnumerable<PluginPageInfo> GetPages()
	{
		return new PluginPageInfo[2]
		{
			new PluginPageInfo
			{
				Name = "HallPassConfiguration",
				EmbeddedResourcePath = GetType().Namespace + ".HallPassConfiguration.html",
				EnableInMainMenu = true,
				MenuIcon = "explicit"
			},
			new PluginPageInfo
			{
				Name = "HallPassConfigurationjs",
				EmbeddedResourcePath = GetType().Namespace + ".HallPassConfiguration.js"
			}
		};
	}
}
