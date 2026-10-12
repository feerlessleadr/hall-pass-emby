using System;
using System.IO;
using System.Linq;
using HallPass.Core;

namespace HallPass.Services;

/// <summary>The plugin's shared state: the evidence store and its file, and the engine settings worked out from the saved configuration.</summary>
internal static class HallPassState
{
	private static readonly object Gate = new object();

	private static EvidenceStore? _store;

	private static string FilePath => Path.Combine(Plugin.Instance!.AppPaths.PluginConfigurationsPath, "HallPass.Evidence.json");

	public static PluginConfiguration Config => Plugin.Instance!.Configuration;

	public static EvidenceStore Store
	{
		get
		{
			lock (Gate)
			{
				if (_store == null)
				{
					string? text = null;
					try
					{
						text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
					}
					catch (IOException ex)
					{
						Plugin.Log("Could not read the saved results, starting empty: " + ex.Message);
					}
					_store = EvidenceStore.FromJson(text);
				}
				return _store;
			}
		}
	}

	/// <summary>Writes the store to its file (to a temporary file first, so a crash never leaves half a file).</summary>
	public static void Save()
	{
		lock (Gate)
		{
			if (_store == null)
			{
				return;
			}
			try
			{
				string temp = FilePath + ".tmp";
				File.WriteAllText(temp, _store.ToJson());
				if (File.Exists(FilePath))
				{
					File.Replace(temp, FilePath, null);
				}
				else
				{
					File.Move(temp, FilePath);
				}
			}
			catch (IOException ex)
			{
				Plugin.Log("Could not save the results: " + ex.Message);
			}
		}
	}

	public static DecisionSettings DecisionSettingsFor(PluginConfiguration c)
	{
		DecisionSettings preset = c.Preset switch
		{
			"Conservative" => DecisionSettings.Conservative,
			"Relaxed" => DecisionSettings.Relaxed,
			_ => DecisionSettings.Balanced,
		};
		return new DecisionSettings
		{
			AutoExplicit = preset.AutoExplicit,
			AutoClean = preset.AutoClean,
			ConflictExplicitWins = preset.ConflictExplicitWins,
			MinCleanSources = preset.MinCleanSources,
			LikelyClean = c.LikelyCleanConfidence,
		};
	}

	public static ScannerOptions ScannerOptionsFor(PluginConfiguration c)
	{
		return new ScannerOptions { MinWordsForClean = c.MinWordsForClean, PerTermCap = c.PerTermCap };
	}

	/// <summary>Changes when anything that shapes the evidence changes: the word list, the scanner options, the skipped genres.</summary>
	public static string EvidenceVersionFor(PluginConfiguration c)
	{
		ScannerOptions o = ScannerOptionsFor(c);
		return EvidenceStore.Fingerprint(
			WordList.DefaultJson(),
			o.ExplicitScore.ToString(),
			o.SuspectScore.ToString(),
			o.ExplicitConfidence.ToString("0.00"),
			o.SuspectConfidence.ToString("0.00"),
			o.CleanConfidence.ToString("0.00"),
			o.MinWordsForClean.ToString(),
			o.PerTermCap.ToString(),
			string.Join(",", (c.SkipGenres ?? new string[0]).Select((string g) => g.Trim().ToLowerInvariant()).OrderBy((string g) => g, StringComparer.Ordinal)));
	}

	/// <summary>Works every decision out again from the stored evidence (after the thresholds changed), then saves.</summary>
	public static void RedecideAll()
	{
		Store.RedecideAll(DecisionSettingsFor(Config));
		Save();
	}
}
