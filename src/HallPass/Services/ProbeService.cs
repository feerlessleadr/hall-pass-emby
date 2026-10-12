using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Services;

namespace HallPass.Services;

[Authenticated(Roles = "Admin")]
[Route("/HallPass/Info", "GET", Summary = "Plugin name and version.")]
public class InfoRequest : IReturn<object>
{
}

/// <summary>Phase 0 probe: what the plugin can see of one item (tags, locks, lyric streams). Never returns lyric text. Removed before release.</summary>
[Authenticated(Roles = "Admin")]
[Route("/HallPass/Probe/Item", "GET", Summary = "Phase 0: tags, locked fields and lyric streams of an item.")]
public class ProbeItemRequest : IReturn<object>
{
	public string Id { get; set; }
}

/// <summary>Phase 0 probe: add or remove one tag on one item through the library's own update path. Removed before release.</summary>
[Authenticated(Roles = "Admin")]
[Route("/HallPass/Probe/Tag", "POST", Summary = "Phase 0: add or remove a tag on one item.")]
public class ProbeTagRequest : IReturn<object>
{
	public string Id { get; set; }

	public string Tag { get; set; }

	public bool Remove { get; set; }

	public bool LockTags { get; set; }

	public bool SaveMetadata { get; set; } = true;
}

public class ProbeService : IService
{
	private static Plugin P => Plugin.Instance ?? throw new InvalidOperationException("Hall Pass is not started.");

	private static BaseItem FindItem(string id)
	{
		if (!long.TryParse(id, out long internalId))
		{
			throw new ArgumentException("Id must be Emby's internal item id (a number).");
		}
		return P.LibraryManager.GetItemById(internalId) ?? throw new ArgumentException("No such item.");
	}

	public object Get(InfoRequest request)
	{
		return new
		{
			Name = P.Name,
			Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
			ApiVersion = 0
		};
	}

	public async Task<object> Get(ProbeItemRequest request)
	{
		BaseItem item = FindItem(request.Id);
		List<object> lyricStreams = new List<object>();
		List<MediaSourceInfo> sources = P.MediaSourceManager.GetStaticMediaSources(item, true, false, null);
		MediaSourceInfo source = sources.FirstOrDefault();
		if (source != null)
		{
			foreach (MediaStream stream in source.MediaStreams.Where((MediaStream s) => s.Type == MediaStreamType.Subtitle))
			{
				string text = null;
				string error = null;
				try
				{
					string input = stream.IsExternal ? stream.Path : source.Path;
					text = await P.SubtitleEncoder.GetSubtitles(input, source.Protocol, true, new Dictionary<string, string>(), stream, "txt", 0L, 0L, false, EncodingContext.Static, CancellationToken.None).ConfigureAwait(false);
					if (text != null && System.IO.File.Exists(text))
					{
						text = System.IO.File.ReadAllText(text);
					}
				}
				catch (Exception ex)
				{
					error = ex.GetType().Name + ": " + ex.Message;
				}
				lyricStreams.Add(new
				{
					stream.Index,
					stream.Codec,
					stream.IsExternal,
					stream.Title,
					Chars = text?.Length,
					Lines = text == null ? (int?)null : text.Split('\n').Length,
					Error = error
				});
			}
		}
		return new
		{
			item.Name,
			Type = item.GetType().Name,
			item.Path,
			Tags = item.Tags,
			LockedFields = item.LockedFields.Select((MetadataFields f) => f.ToString()).ToArray(),
			ProviderIds = item.ProviderIds,
			LyricStreams = lyricStreams
		};
	}

	public object Post(ProbeTagRequest request)
	{
		if (string.IsNullOrWhiteSpace(request.Tag))
		{
			throw new ArgumentException("Tag is required.");
		}
		BaseItem item = FindItem(request.Id);
		string[] before = item.Tags;
		List<string> tags = before.ToList();
		tags.RemoveAll((string t) => string.Equals(t, request.Tag, StringComparison.OrdinalIgnoreCase));
		if (!request.Remove)
		{
			tags.Add(request.Tag.Trim());
		}
		item.SetTags(tags);
		if (request.LockTags && !item.LockedFields.Contains(MetadataFields.Tags))
		{
			item.LockedFields = item.LockedFields.Concat(new[] { MetadataFields.Tags }).ToArray();
		}
		P.LibraryManager.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataEdit, true, request.SaveMetadata, null, CancellationToken.None);
		BaseItem reread = P.LibraryManager.GetItemById(item.InternalId);
		return new
		{
			Before = before,
			After = reread.Tags,
			LockedFields = reread.LockedFields.Select((MetadataFields f) => f.ToString()).ToArray()
		};
	}
}
