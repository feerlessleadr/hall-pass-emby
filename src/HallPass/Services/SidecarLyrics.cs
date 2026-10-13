using System;
using System.IO;
using HallPass.Core;

namespace HallPass.Services;

/// <summary>
/// Lyrics kept in a plain file next to the track (<c>Song.lrc</c> or <c>Song.txt</c>) that Emby did not list as a lyric stream: read straight from the
/// folder, scanned in memory and never stored. Looks only in the track's own folder, only at files named like the track, and reads at most 512 KB.
/// </summary>
internal static class SidecarLyrics
{
	private static readonly string[] Extensions = { ".lrc", ".txt" };

	private const long MaxBytes = 512 * 1024;

	/// <summary>The lyric file next to the track and its text (timing marks removed), or null when there is none or it is empty.</summary>
	public static (string? Text, string? File) Read(string? audioPath)
	{
		string? file = Find(audioPath);
		if (file == null)
		{
			return (null, null);
		}
		try
		{
			FileInfo info = new FileInfo(file);
			if (info.Length == 0 || info.Length > MaxBytes)
			{
				return (null, null);
			}
			string text = File.ReadAllText(file);
			if (file.EndsWith(".lrc", StringComparison.OrdinalIgnoreCase))
			{
				text = OnlineLyrics.StripTimestamps(text);
			}
			return string.IsNullOrWhiteSpace(text) ? (null, null) : (text, file);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Plugin.Log("Could not read a lyric file next to " + Path.GetFileName(audioPath) + ": " + ex.GetType().Name);
			return (null, null);
		}
	}

	/// <summary>Changes when a lyric file next to the track appears, goes or is edited (so the track is looked at again).</summary>
	public static string Stamp(string? audioPath)
	{
		string? file = Find(audioPath);
		if (file == null)
		{
			return string.Empty;
		}
		try
		{
			FileInfo info = new FileInfo(file);
			return Path.GetExtension(file).ToLowerInvariant() + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
		}
		catch (IOException)
		{
			return string.Empty;
		}
	}

	private static string? Find(string? audioPath)
	{
		if (string.IsNullOrEmpty(audioPath))
		{
			return null;
		}
		try
		{
			string? dir = Path.GetDirectoryName(audioPath);
			string name = Path.GetFileNameWithoutExtension(audioPath);
			if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir))
			{
				return null;
			}
			// the same name with a lyric extension, in any letter case (a Linux folder is case sensitive)
			foreach (string ext in Extensions)
			{
				string exact = Path.Combine(dir, name + ext);
				if (File.Exists(exact))
				{
					return exact;
				}
			}
			foreach (string candidate in Directory.EnumerateFiles(dir))
			{
				string ext = Path.GetExtension(candidate);
				if (Array.Exists(Extensions, (string e) => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))
					&& string.Equals(Path.GetFileNameWithoutExtension(candidate), name, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}
			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
