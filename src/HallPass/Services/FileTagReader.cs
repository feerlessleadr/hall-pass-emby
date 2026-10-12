using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HallPass.Core;

namespace HallPass.Services;

/// <summary>What was read from inside one audio file: its tag fields and, for MP4 files, the advisory atom.</summary>
internal sealed record FileTagInfo(IReadOnlyDictionary<string, string> Tags, int? RtngAtom)
{
	public static FileTagInfo Empty => new FileTagInfo(new Dictionary<string, string>(), null);
}

/// <summary>Reads a file's tags with Emby's own ffprobe (no extra library) and, for M4A/MP4, looks for the iTunes advisory atom directly. Only reads.</summary>
internal static class FileTagReader
{
	private const int AtomWindow = 8 * 1024 * 1024;

	public static async Task<FileTagInfo> ReadAsync(string path, CancellationToken token)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			return FileTagInfo.Empty;
		}
		IReadOnlyDictionary<string, string> tags = await RunProbeAsync(path, token).ConfigureAwait(false);
		int? rtng = null;
		string ext = Path.GetExtension(path).ToLowerInvariant();
		if (ext is ".m4a" or ".mp4" or ".m4b" or ".aac" or ".alac")
		{
			rtng = ReadRtng(path);
		}
		return new FileTagInfo(tags, rtng);
	}

	private static async Task<IReadOnlyDictionary<string, string>> RunProbeAsync(string path, CancellationToken token)
	{
		string? probe = Plugin.Instance?.FfmpegManager.FfmpegConfiguration.ProbePath;
		if (string.IsNullOrEmpty(probe) || !File.Exists(probe))
		{
			return new Dictionary<string, string>();
		}
		ProcessStartInfo start = new ProcessStartInfo(probe)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (string arg in new[] { "-v", "error", "-show_entries", "format_tags:stream_tags", "-of", "json", "-i", path })
		{
			start.ArgumentList.Add(arg);
		}
		try
		{
			using Process process = new Process { StartInfo = start };
			process.Start();
			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> errors = process.StandardError.ReadToEndAsync();
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(30));
			try
			{
				await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				try
				{
					process.Kill(true);
				}
				catch (InvalidOperationException)
				{
				}
				token.ThrowIfCancellationRequested();
				Plugin.Log("ffprobe took too long for " + path);
				return new Dictionary<string, string>();
			}
			return EmbeddedTags.ParseProbeJson(await output.ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			Plugin.Log("Could not read the tags of " + path + ": " + ex.GetType().Name + " " + ex.Message);
			return new Dictionary<string, string>();
		}
	}

	/// <summary>Looks in the first and last 8 MB of an MP4 file (where the metadata box sits) for the advisory atom.</summary>
	private static int? ReadRtng(string path)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			byte[] buffer = new byte[(int)Math.Min(stream.Length, AtomWindow)];
			int read = stream.Read(buffer, 0, buffer.Length);
			int? found = EmbeddedTags.FindRtngAtom(buffer.AsSpan(0, read));
			if (found == null && stream.Length > AtomWindow)
			{
				stream.Seek(-Math.Min(stream.Length - AtomWindow, AtomWindow), SeekOrigin.End);
				read = stream.Read(buffer, 0, buffer.Length);
				found = EmbeddedTags.FindRtngAtom(buffer.AsSpan(0, read));
			}
			return found;
		}
		catch (IOException)
		{
			return null;
		}
	}
}
