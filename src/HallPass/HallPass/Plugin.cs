using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace HallPass;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public ILibraryManager LibraryManager { get; }

	public IMediaSourceManager MediaSourceManager { get; }

	public ISubtitleEncoder SubtitleEncoder { get; }

	public override string Name => "Hall Pass";

	public override string Description => "Finds explicit music and tags it, so a smart playlist can leave it out.";

	// Never changes: Emby identifies the plugin by this id.
	public override Guid Id => Guid.Parse("6ca80343-2b60-40de-ab81-7a41628fcaf2");

	public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager, ISubtitleEncoder subtitleEncoder)
		: base(appPaths, xmlSerializer)
	{
		Instance = this;
		LibraryManager = libraryManager;
		MediaSourceManager = mediaSourceManager;
		SubtitleEncoder = subtitleEncoder;
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
