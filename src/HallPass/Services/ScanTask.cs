using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace HallPass.Services;

/// <summary>
/// The scan as one of Emby's scheduled tasks (Dashboard > Scheduled Tasks > Hall Pass): by default every night at 3:30, looking only at new or changed
/// tracks. With "apply after scan" on and dry run off it also writes the tags afterwards. Change or remove the schedule there like any other task.
/// </summary>
public class ScanTask : IScheduledTask
{
	public string Name => "Scan music for explicit content";

	public string Key => "HallPassScan";

	public string Description => "Looks at new and changed music tracks and records whether each is explicit, likely clean or clean. Writes tags afterwards only if you turned that on in the Hall Pass settings.";

	public string Category => "Hall Pass";

	public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
	{
		return new[]
		{
			new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks }
		};
	}

	public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
	{
		await ScanJob.RunScheduledAsync(progress, cancellationToken).ConfigureAwait(false);
		PluginConfiguration config = HallPassState.Config;
		if (config.ApplyAfterScan && !config.DryRun && !cancellationToken.IsCancellationRequested)
		{
			try
			{
				WriteJob.StartApply();
				using (cancellationToken.Register(WriteJob.Cancel))
				{
					while (WriteJob.Status.State == "Running")
					{
						await Task.Delay(1000, CancellationToken.None).ConfigureAwait(false);
					}
				}
			}
			catch (InvalidOperationException ex)
			{
				Plugin.Log("The scheduled scan finished but the tags were not written: " + ex.Message);
			}
		}
		progress.Report(100);
	}
}
