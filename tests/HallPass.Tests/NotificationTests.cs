using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

internal static class NotificationFiles
{
	/// <summary>Finds a file under the repository by walking up from the test's folder.</summary>
	public static string RepoFile(params string[] parts)
	{
		string? dir = AppContext.BaseDirectory;
		while (dir != null)
		{
			string candidate = Path.Combine(new[] { dir }.Concat(parts).ToArray());
			if (File.Exists(candidate))
			{
				return candidate;
			}
			dir = Path.GetDirectoryName(dir);
		}
		throw new FileNotFoundException("Not found from " + AppContext.BaseDirectory + ": " + string.Join("/", parts));
	}

	public static string TempFolder()
	{
		string path = Path.Combine(Path.GetTempPath(), "hallpass-notif-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}
}

public class EventLogTests
{
	private static PendingEvent Event(string name = "ScanFinished") => new PendingEvent(name, new Dictionary<string, object?> { ["Total"] = 1 });

	[Fact]
	public void SequenceNumbersGrowByOneAndTheCommonFieldsAreThere()
	{
		string folder = NotificationFiles.TempFolder();
		EventLog log = new EventLog(Path.Combine(folder, "events.json"), () => new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc));
		NotificationEvent a = log.Append(Event());
		NotificationEvent b = log.Append(Event(EventNames.ScanFailed));
		Assert.Equal(1, a.Seq);
		Assert.Equal(2, b.Seq);
		Assert.Equal("HallPass.ScanFailed", b.Name);
		Assert.Equal(1, b.Data.GetProperty("ApiLevel").GetInt32());
		Assert.Equal(2, b.Data.GetProperty("Seq").GetInt64());
		Assert.Equal("ScanFailed", b.Data.GetProperty("Event").GetString());
		Assert.Equal("2026-10-14T12:00:00Z", b.Data.GetProperty("ServerTimeUtc").GetString());
		Assert.True(Guid.TryParse(b.Data.GetProperty("EventId").GetString(), out _));
		Assert.Equal(1, b.Data.GetProperty("Total").GetInt32());
	}

	[Fact]
	public void OnlyTheLast500AreKeptAndAskingTooFarBackSaysTruncated()
	{
		string folder = NotificationFiles.TempFolder();
		EventLog log = new EventLog(Path.Combine(folder, "events.json"));
		for (int i = 0; i < 520; i++)
		{
			log.Append(Event());
		}
		Assert.Equal(520, log.LatestSeq);
		IReadOnlyList<NotificationEvent> all = log.After(0, 500, out bool truncated);
		Assert.True(truncated);
		Assert.Equal(500, all.Count);
		Assert.Equal(21, all[0].Seq);
		log.After(20, 10, out bool exact);
		Assert.False(exact);
		log.After(520, 10, out bool upToDate);
		Assert.False(upToDate);
	}

	[Fact]
	public void AfterReturnsOldestFirstAndHonorsTheLimit()
	{
		string folder = NotificationFiles.TempFolder();
		EventLog log = new EventLog(Path.Combine(folder, "events.json"));
		for (int i = 0; i < 10; i++)
		{
			log.Append(Event());
		}
		IReadOnlyList<NotificationEvent> got = log.After(3, 4, out bool truncated);
		Assert.False(truncated);
		Assert.Equal(new long[] { 4, 5, 6, 7 }, got.Select((NotificationEvent e) => e.Seq).ToArray());
	}

	[Fact]
	public void NumbersAndEventsSurviveARestart()
	{
		string folder = NotificationFiles.TempFolder();
		string path = Path.Combine(folder, "events.json");
		EventLog first = new EventLog(path);
		first.Append(Event());
		first.Append(Event());
		EventLog second = new EventLog(path);
		Assert.Equal(2, second.LatestSeq);
		Assert.Equal(3, second.Append(Event()).Seq);
		Assert.Equal(3, second.After(0, 100, out _).Count);
	}

	[Fact]
	public void NumbersNeverGoBackWhenTheLogFileIsDamaged()
	{
		string folder = NotificationFiles.TempFolder();
		string path = Path.Combine(folder, "events.json");
		EventLog first = new EventLog(path);
		for (int i = 0; i < 7; i++)
		{
			first.Append(Event());
		}
		File.WriteAllText(path, "{ this is not json");
		EventLog second = new EventLog(path);
		Assert.Equal(7, second.LatestSeq);
		Assert.Equal(8, second.Append(Event()).Seq);
		Assert.True(File.Exists(path + ".corrupt"));
	}

	[Fact]
	public void NumbersNeverGoBackWhenBothFilesAreLost()
	{
		string folder = NotificationFiles.TempFolder();
		string path = Path.Combine(folder, "events.json");
		EventLog first = new EventLog(path);
		first.Append(Event());
		File.WriteAllText(path, "garbage");
		File.Delete(path + ".seq");
		EventLog second = new EventLog(path, () => new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc));
		Assert.True(second.LatestSeq > 1_000_000_000);
	}
}

public class EventAggregatorTests
{
	private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

	private static EventAggregator New() => new EventAggregator(new NotificationState());

	private static NotificationSettings AllOn() => new NotificationSettings { ReviewQueue = true };

	private static ScanOutcome Scan(string state, RunOrigin origin, int scanned, int total = 100) => new ScanOutcome
	{
		State = state, Origin = origin, Total = total, Scanned = scanned, Unchanged = total - scanned, Failed = 1, NeedsReview = 40, OnlineFailed = 2,
	};

	[Fact]
	public void AScanTheOwnerStartedAlwaysReportsEvenWhenNothingChanged()
	{
		List<PendingEvent> events = New().ScanEnded(Scan("Completed", RunOrigin.Manual, 0), AllOn());
		PendingEvent e = Assert.Single(events);
		Assert.Equal(EventNames.ScanFinished, e.Event);
		Assert.Equal(100, e.Fields["Total"]);
		Assert.Equal(0, e.Fields["Scanned"]);
		Assert.Equal(100, e.Fields["Unchanged"]);
		Assert.Equal(1, e.Fields["Failed"]);
		Assert.Equal(40, e.Fields["NeedsReview"]);
		Assert.Equal(2, e.Fields["OnlineFailed"]);
	}

	[Fact]
	public void AQuietScheduledScanSendsNothingAndABigOneReports()
	{
		Assert.Empty(New().ScanEnded(Scan("Completed", RunOrigin.Scheduled, 0), AllOn()));
		Assert.Empty(New().ScanEnded(Scan("Completed", RunOrigin.Scheduled, 24), AllOn()));
		Assert.Single(New().ScanEnded(Scan("Completed", RunOrigin.Scheduled, 25), AllOn()));
	}

	[Fact]
	public void AFailedOrCancelledScanReportsWhoeverStartedIt()
	{
		foreach (RunOrigin origin in new[] { RunOrigin.Manual, RunOrigin.Scheduled })
		{
			ScanOutcome failed = Scan("Failed", origin, 0);
			failed.Message = "The scan failed: Emby is shutting down.";
			PendingEvent e = Assert.Single(New().ScanEnded(failed, AllOn()));
			Assert.Equal(EventNames.ScanFailed, e.Event);
			Assert.Equal("Failed", e.Fields["State"]);
			Assert.Equal("The scan failed: Emby is shutting down.", e.Fields["Message"]);
		}
		PendingEvent cancelled = Assert.Single(New().ScanEnded(Scan("Cancelled", RunOrigin.Manual, 3), AllOn()));
		Assert.Equal("Cancelled", cancelled.Fields["State"]);
	}

	[Fact]
	public void TheAutomaticScanMakesOneSummaryAcrossBatchesAndOnlyWhenItWasBig()
	{
		EventAggregator agg = New();
		agg.AutoBatch(50, 20, 30, 0, 0);
		Assert.Empty(agg.AutoFlush(5, AllOn())); // 20 changed: quiet
		agg.AutoBatch(50, 20, 28, 2, 1);
		agg.AutoBatch(50, 20, 30, 0, 2);
		Assert.True(agg.AutoPending);
		PendingEvent e = Assert.Single(agg.AutoFlush(77, AllOn()));
		Assert.Equal(100, e.Fields["Total"]);
		Assert.Equal(40, e.Fields["Scanned"]);
		Assert.Equal(58, e.Fields["Unchanged"]);
		Assert.Equal(2, e.Fields["Failed"]);
		Assert.Equal(77, e.Fields["NeedsReview"]);
		Assert.Equal(3, e.Fields["OnlineFailed"]);
		Assert.False(agg.AutoPending);
		Assert.Empty(agg.AutoFlush(77, AllOn())); // never repeated
	}

	[Fact]
	public void AWriteJobReportsKindAndCounts()
	{
		WriteOutcome apply = new WriteOutcome { State = "Completed", Kind = "Apply", Changed = 10, Unchanged = 5, Failed = 1, Conflicts = 0 };
		PendingEvent e = Assert.Single(New().WriteEnded(apply, AllOn()));
		Assert.Equal(EventNames.ApplyFinished, e.Event);
		Assert.Equal("Apply", e.Fields["Kind"]);
		Assert.Equal(10, e.Fields["Changed"]);
		WriteOutcome undo = new WriteOutcome { State = "Completed", Kind = "Undo", Changed = 3, Conflicts = 2 };
		PendingEvent u = Assert.Single(New().WriteEnded(undo, AllOn()));
		Assert.Equal("Undo", u.Fields["Kind"]);
		Assert.Equal(2, u.Fields["Conflicts"]);
	}

	[Fact]
	public void AutomaticWritingIsQuietUnlessItChangedManyTracks()
	{
		Assert.Empty(New().WriteEnded(new WriteOutcome { State = "Completed", Automatic = true, Changed = 3 }, AllOn()));
		Assert.Single(New().WriteEnded(new WriteOutcome { State = "Completed", Automatic = true, Changed = 25 }, AllOn()));
	}

	[Fact]
	public void AFailedOrCancelledWriteJobReportsEvenWhenAutomatic()
	{
		PendingEvent failed = Assert.Single(New().WriteEnded(new WriteOutcome { State = "Failed", Kind = "Apply", Automatic = true, Message = "The apply failed: disk error." }, AllOn()));
		Assert.Equal(EventNames.ApplyFailed, failed.Event);
		Assert.Equal("Failed", failed.Fields["State"]);
		Assert.Equal("The apply failed: disk error.", failed.Fields["Message"]);
		PendingEvent cancelled = Assert.Single(New().WriteEnded(new WriteOutcome { State = "Cancelled", Kind = "Undo" }, AllOn()));
		Assert.Equal("Undo", cancelled.Fields["Kind"]);
		Assert.Equal("Cancelled", cancelled.Fields["State"]);
	}

	[Fact]
	public void AnInterruptedJobIsAnnouncedOnceAndSeveralMakeOneEvent()
	{
		EventAggregator agg = New();
		UnfinishedJob newest = new UnfinishedJob { JobId = "bbb", Kind = "auto", StartedUtc = T0 };
		UnfinishedJob older = new UnfinishedJob { JobId = "aaa", Kind = "undo", StartedUtc = T0.AddDays(-2) };
		PendingEvent e = Assert.Single(agg.UnfinishedFound(new[] { older, newest }, AllOn()));
		Assert.Equal(EventNames.ApplyFailed, e.Event);
		Assert.Equal("Apply", e.Fields["Kind"]); // the newest one (an automatic apply)
		Assert.Equal("Interrupted", e.Fields["State"]);
		Assert.Contains("1 older unfinished job was found as well", (string)e.Fields["Message"]!);
		Assert.Empty(agg.UnfinishedFound(new[] { older, newest }, AllOn())); // a later start does not repeat it
		UnfinishedJob another = new UnfinishedJob { JobId = "ccc", Kind = "undo", StartedUtc = T0.AddHours(1) };
		PendingEvent second = Assert.Single(agg.UnfinishedFound(new[] { older, newest, another }, AllOn()));
		Assert.Equal("Undo", second.Fields["Kind"]);
	}

	[Fact]
	public void AnAnnouncedUnfinishedJobIsForgottenWhenItsFileIsGone()
	{
		NotificationState state = new NotificationState();
		EventAggregator agg = new EventAggregator(state);
		agg.UnfinishedFound(new[] { new UnfinishedJob { JobId = "aaa", Kind = "apply", StartedUtc = T0 } }, AllOn());
		Assert.Single(state.AnnouncedUnfinished);
		agg.UnfinishedFound(Array.Empty<UnfinishedJob>(), AllOn());
		Assert.Empty(state.AnnouncedUnfinished);
	}

	[Fact]
	public void ReviewQueueIsAnnouncedOnlyWhenItGrewAndAtMostOnceInTenMinutes()
	{
		EventAggregator agg = New();
		Assert.Empty(agg.ReviewQueueChanged(100, T0, AllOn(), false)); // the first look only sets the baseline
		Assert.Empty(agg.ReviewQueueChanged(100, T0.AddMinutes(20), AllOn(), false)); // no change
		PendingEvent e = Assert.Single(agg.ReviewQueueChanged(130, T0.AddMinutes(30), AllOn(), false));
		Assert.Equal(EventNames.ReviewQueue, e.Event);
		Assert.Equal(130, e.Fields["Count"]);
		Assert.Equal(30, e.Fields["New"]);
		Assert.Empty(agg.ReviewQueueChanged(140, T0.AddMinutes(35), AllOn(), false)); // within ten minutes
		PendingEvent later = Assert.Single(agg.ReviewQueueChanged(140, T0.AddMinutes(41), AllOn(), false)); // the held-back rise comes out
		Assert.Equal(10, later.Fields["New"]);
	}

	[Fact]
	public void ReviewQueueGoingDownLowersTheBaselineSoLaterRisesCount()
	{
		EventAggregator agg = New();
		agg.ReviewQueueChanged(100, T0, AllOn(), false);
		Assert.Empty(agg.ReviewQueueChanged(60, T0.AddHours(1), AllOn(), false));
		PendingEvent e = Assert.Single(agg.ReviewQueueChanged(70, T0.AddHours(2), AllOn(), false));
		Assert.Equal(10, e.Fields["New"]);
	}

	[Fact]
	public void ASilentLookOnlyRemembersTheNumber()
	{
		EventAggregator agg = New();
		agg.ReviewQueueChanged(100, T0, AllOn(), false);
		Assert.Empty(agg.ReviewQueueChanged(500, T0.AddHours(1), AllOn(), true));
		Assert.Empty(agg.ReviewQueueChanged(500, T0.AddHours(2), AllOn(), false));
	}

	[Fact]
	public void ASwitchThatIsOffKeepsTheBookkeepingButSendsNothing()
	{
		NotificationSettings off = new NotificationSettings { ReviewQueue = false };
		EventAggregator agg = New();
		agg.ReviewQueueChanged(10, T0, off, false);
		Assert.Empty(agg.ReviewQueueChanged(500, T0.AddHours(1), off, false));
		// switching it on later does not announce the old rise
		Assert.Empty(agg.ReviewQueueChanged(500, T0.AddHours(2), AllOn(), false));
	}

	[Fact]
	public void TheMasterSwitchAndEachEventSwitchAreHonored()
	{
		NotificationSettings master = new NotificationSettings { SendPhoneAlerts = false, ReviewQueue = true };
		Assert.Empty(New().ScanEnded(Scan("Completed", RunOrigin.Manual, 5), master));
		Assert.Empty(New().ScanEnded(Scan("Failed", RunOrigin.Manual, 5), master));
		Assert.Empty(New().WriteEnded(new WriteOutcome { State = "Completed", Changed = 5 }, master));
		Assert.Empty(New().WriteEnded(new WriteOutcome { State = "Failed" }, master));
		Assert.Empty(New().UnfinishedFound(new[] { new UnfinishedJob { JobId = "a", Kind = "apply", StartedUtc = T0 } }, master));

		Assert.Empty(New().ScanEnded(Scan("Completed", RunOrigin.Manual, 5), new NotificationSettings { ScanFinished = false }));
		Assert.Empty(New().ScanEnded(Scan("Failed", RunOrigin.Manual, 5), new NotificationSettings { ScanFailed = false }));
		Assert.Empty(New().WriteEnded(new WriteOutcome { State = "Completed", Changed = 5 }, new NotificationSettings { ApplyFinished = false }));
		Assert.Empty(New().WriteEnded(new WriteOutcome { State = "Failed" }, new NotificationSettings { ApplyFailed = false }));
		Assert.Single(New().ScanEnded(Scan("Failed", RunOrigin.Manual, 5), new NotificationSettings { ScanFinished = false }));
	}

	[Fact]
	public void TheDefaultSwitchesAreOnExceptTheReviewQueue()
	{
		NotificationSettings s = new NotificationSettings();
		Assert.True(s.SendPhoneAlerts);
		Assert.True(s.IsOn(EventNames.ScanFinished));
		Assert.True(s.IsOn(EventNames.ScanFailed));
		Assert.True(s.IsOn(EventNames.ApplyFinished));
		Assert.True(s.IsOn(EventNames.ApplyFailed));
		Assert.False(s.IsOn(EventNames.ReviewQueue));
		Assert.False(s.IsOn("SomethingElse"));
	}

	[Theory]
	[InlineData(@"Could not find file 'C:\Users\someone\Music\Album One\track.mp3'.")]
	[InlineData(@"Access to the path 'D:\media\Music' is denied.")]
	[InlineData(@"Could not reach \\nas\share\Music\x.flac now")]
	[InlineData("The folder /mnt/media/Music/Artist/Album was not readable.")]
	[InlineData(@"Failed at C:\Music\a b c\d.mp3, then stopped")]
	public void StripPathsRemovesDrivesSharesAndUnixPaths(string text)
	{
		string stripped = EventAggregator.StripPaths(text);
		Assert.DoesNotContain(@":\", stripped);
		Assert.DoesNotContain(@"\\", stripped);
		Assert.DoesNotContain("/mnt", stripped);
		Assert.DoesNotContain("someone", stripped);
		Assert.DoesNotContain("share", stripped.Replace("(a folder)", string.Empty));
		Assert.Contains("(a folder)", stripped);
	}

	[Fact]
	public void StripPathsLeavesPlainSentencesAndSlashesInWordsAlone()
	{
		Assert.Equal("The scan failed: and/or the network was down (50/50).", EventAggregator.StripPaths("The scan failed: and/or the network was down (50/50)."));
	}

	[Fact]
	public void NoPayloadEverCarriesAPath()
	{
		ScanOutcome failed = Scan("Failed", RunOrigin.Manual, 0);
		failed.Message = @"The scan failed: Could not read 'C:\Users\someone\Music\Secret Album\a.mp3' on \\nas\music";
		WriteOutcome writeFailed = new WriteOutcome { State = "Failed", Message = "The apply failed: Access to the path '/data/media/Music/x.mp3' is denied." };
		List<PendingEvent> all = New().ScanEnded(failed, AllOn());
		all.AddRange(New().WriteEnded(writeFailed, AllOn()));
		string json = JsonSerializer.Serialize(all.Select((PendingEvent e) => e.Fields));
		Assert.DoesNotContain("Secret Album", json);
		Assert.DoesNotContain("someone", json);
		Assert.DoesNotContain("/data/media", json);
		Assert.DoesNotContain("nas", json);
	}

	[Fact]
	public void LongMessagesAreCut()
	{
		ScanOutcome failed = Scan("Failed", RunOrigin.Manual, 0);
		failed.Message = new string('x', 2000);
		PendingEvent e = Assert.Single(New().ScanEnded(failed, AllOn()));
		Assert.True(((string)e.Fields["Message"]!).Length <= EventAggregator.MaxMessageLength);
	}

	[Fact]
	public void TheMemoryOfTheAggregatorSurvivesARestart()
	{
		NotificationState state = new NotificationState();
		EventAggregator agg = new EventAggregator(state);
		agg.ReviewQueueChanged(100, T0, AllOn(), false);
		agg.ReviewQueueChanged(120, T0.AddHours(1), AllOn(), false);
		agg.UnfinishedFound(new[] { new UnfinishedJob { JobId = "abc", Kind = "apply", StartedUtc = T0 } }, AllOn());

		NotificationState copy = JsonSerializer.Deserialize<NotificationState>(JsonSerializer.Serialize(state))!;
		EventAggregator restarted = new EventAggregator(copy);
		Assert.Empty(restarted.ReviewQueueChanged(120, T0.AddHours(2), AllOn(), false));
		Assert.Empty(restarted.UnfinishedFound(new[] { new UnfinishedJob { JobId = "abc", Kind = "apply", StartedUtc = T0 } }, AllOn()));
		Assert.Single(restarted.ReviewQueueChanged(150, T0.AddHours(3), AllOn(), false));
	}
}

public class NotificationCatalogTests
{
	private static JsonDocument Samples() => JsonDocument.Parse(File.ReadAllText(NotificationFiles.RepoFile("docs", "reference", "notification-samples.json")));

	/// <summary>True when two JSON values are the same (objects compared by key, ignoring order).</summary>
	private static bool Same(JsonElement a, JsonElement b)
	{
		if (a.ValueKind != b.ValueKind)
		{
			return false;
		}
		switch (a.ValueKind)
		{
		case JsonValueKind.Object:
			List<JsonProperty> pa = a.EnumerateObject().ToList();
			List<JsonProperty> pb = b.EnumerateObject().ToList();
			return pa.Count == pb.Count && pa.All((JsonProperty p) => b.TryGetProperty(p.Name, out JsonElement other) && Same(p.Value, other));
		case JsonValueKind.Array:
			List<JsonElement> la = a.EnumerateArray().ToList();
			List<JsonElement> lb = b.EnumerateArray().ToList();
			return la.Count == lb.Count && la.Zip(lb, Same).All((bool x) => x);
		case JsonValueKind.String:
			return a.GetString() == b.GetString();
		case JsonValueKind.Number:
			return a.GetDouble() == b.GetDouble();
		default:
			return true;
		}
	}

	[Fact]
	public void TheSampleFileAndTheRealCatalogAreTheSameThing()
	{
		using JsonDocument sample = Samples();
		JsonElement real = JsonSerializer.SerializeToElement(NotificationCatalog.Build());
		Assert.True(Same(sample.RootElement.GetProperty("catalog"), real), "docs/reference/notification-samples.json (catalog) and NotificationCatalog.Build() differ: change both together.");
	}

	[Fact]
	public void TheCatalogHasTheFiveEventsWithTheContractsDefaults()
	{
		JsonElement real = JsonSerializer.SerializeToElement(NotificationCatalog.Build());
		Assert.Equal("HallPass", real.GetProperty("Service").GetProperty("Id").GetString());
		Dictionary<string, JsonElement> events = real.GetProperty("Events").EnumerateArray().ToDictionary((JsonElement e) => e.GetProperty("Id").GetString()!);
		Assert.Equal(new[] { "HallPass.ApplyFailed", "HallPass.ApplyFinished", "HallPass.ReviewQueue", "HallPass.ScanFailed", "HallPass.ScanFinished" }, events.Keys.OrderBy((string k) => k, StringComparer.Ordinal).ToArray());
		foreach (string id in new[] { "HallPass.ScanFinished", "HallPass.ScanFailed", "HallPass.ApplyFinished", "HallPass.ApplyFailed" })
		{
			Assert.True(events[id].GetProperty("DefaultRule").GetProperty("Enabled").GetBoolean());
			Assert.Empty(events[id].GetProperty("Params").EnumerateArray());
		}
		JsonElement review = events["HallPass.ReviewQueue"];
		Assert.False(review.GetProperty("DefaultRule").GetProperty("Enabled").GetBoolean());
		JsonElement p = Assert.Single(review.GetProperty("Params").EnumerateArray());
		Assert.Equal("MinCount", p.GetProperty("Name").GetString());
		Assert.Equal("int", p.GetProperty("Type").GetString());
		Assert.Equal(1, p.GetProperty("Min").GetInt32());
		Assert.Equal(100000, p.GetProperty("Max").GetInt32());
		Assert.Equal(50, p.GetProperty("Default").GetInt32());
	}

	[Fact]
	public void TheSettingsDefaultsMatchTheCatalogsDefaultRules()
	{
		NotificationSettings s = new NotificationSettings();
		JsonElement real = JsonSerializer.SerializeToElement(NotificationCatalog.Build());
		foreach (JsonElement e in real.GetProperty("Events").EnumerateArray())
		{
			string name = e.GetProperty("Id").GetString()!.Substring(EventNames.Prefix.Length);
			Assert.Equal(e.GetProperty("DefaultRule").GetProperty("Enabled").GetBoolean(), s.IsOn(name));
		}
	}

	[Fact]
	public void TheSampleInfoListsTheEventsAndCatalogFeatures()
	{
		using JsonDocument sample = Samples();
		JsonElement info = sample.RootElement.GetProperty("info");
		Assert.Equal(EventNames.ApiLevel, info.GetProperty("ApiLevel").GetInt32());
		string[] features = info.GetProperty("Features").EnumerateArray().Select((JsonElement f) => f.GetString()!).ToArray();
		Assert.Contains("events", features);
		Assert.Contains("catalog", features);
	}

	/// <summary>What each event really carries, made by the aggregator and the log.</summary>
	private static Dictionary<string, NotificationEvent> RealEvents()
	{
		EventAggregator agg = new EventAggregator(new NotificationState());
		NotificationSettings on = new NotificationSettings { ReviewQueue = true };
		DateTime t = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);
		List<PendingEvent> pending = new List<PendingEvent>();
		pending.AddRange(agg.ScanEnded(new ScanOutcome { State = "Completed", Origin = RunOrigin.Manual, Total = 3, Scanned = 3 }, on));
		pending.AddRange(agg.ScanEnded(new ScanOutcome { State = "Failed", Origin = RunOrigin.Manual, Message = "x" }, on));
		pending.AddRange(agg.WriteEnded(new WriteOutcome { State = "Completed", Kind = "Apply", Changed = 1 }, on));
		pending.AddRange(agg.WriteEnded(new WriteOutcome { State = "Failed", Kind = "Apply", Message = "x" }, on));
		agg.ReviewQueueChanged(1, t, on, false);
		pending.AddRange(agg.ReviewQueueChanged(5, t.AddHours(1), on, false));

		string folder = NotificationFiles.TempFolder();
		EventLog log = new EventLog(Path.Combine(folder, "events.json"));
		return pending.Select((PendingEvent p) => log.Append(p)).ToDictionary((NotificationEvent e) => e.Name);
	}

	[Fact]
	public void EverySampleHasExactlyTheFieldsTheRealEventHasAndEveryRealEventHasASample()
	{
		using JsonDocument sample = Samples();
		Dictionary<string, NotificationEvent> real = RealEvents();
		Dictionary<string, JsonElement> samples = sample.RootElement.GetProperty("pushed").EnumerateObject().ToDictionary((JsonProperty p) => p.Name, (JsonProperty p) => p.Value);
		Assert.Equal(real.Keys.OrderBy((string k) => k, StringComparer.Ordinal).ToArray(), samples.Keys.OrderBy((string k) => k, StringComparer.Ordinal).ToArray());
		foreach (KeyValuePair<string, JsonElement> pair in samples)
		{
			string[] sampleFields = pair.Value.EnumerateObject().Select((JsonProperty p) => p.Name).OrderBy((string n) => n, StringComparer.Ordinal).ToArray();
			string[] realFields = real[pair.Key].Data.EnumerateObject().Select((JsonProperty p) => p.Name).OrderBy((string n) => n, StringComparer.Ordinal).ToArray();
			Assert.Equal(realFields, sampleFields);
			Assert.Equal(pair.Key, EventNames.Prefix + pair.Value.GetProperty("Event").GetString());
			// the value kinds agree too (a number stays a number, a string a string)
			foreach (JsonProperty field in pair.Value.EnumerateObject())
			{
				Assert.Equal(real[pair.Key].Data.GetProperty(field.Name).ValueKind, field.Value.ValueKind);
			}
		}
	}

	[Fact]
	public void EveryEventInTheCatalogHasASampleAndNoSampleLacksACatalogEntry()
	{
		using JsonDocument sample = Samples();
		string[] catalogIds = sample.RootElement.GetProperty("catalog").GetProperty("Events").EnumerateArray().Select((JsonElement e) => e.GetProperty("Id").GetString()!).OrderBy((string s) => s, StringComparer.Ordinal).ToArray();
		string[] pushed = sample.RootElement.GetProperty("pushed").EnumerateObject().Select((JsonProperty p) => p.Name).OrderBy((string s) => s, StringComparer.Ordinal).ToArray();
		Assert.Equal(catalogIds, pushed);
	}

	[Fact]
	public void TheCatchUpSampleIsInTheDocumentedShape()
	{
		using JsonDocument sample = Samples();
		JsonElement events = sample.RootElement.GetProperty("events");
		Assert.True(events.GetProperty("LatestSeq").GetInt64() > 0);
		Assert.Equal(JsonValueKind.False, events.GetProperty("Truncated").ValueKind);
		foreach (JsonElement e in events.GetProperty("Events").EnumerateArray())
		{
			Assert.Equal(e.GetProperty("Seq").GetInt64(), e.GetProperty("Data").GetProperty("Seq").GetInt64());
			Assert.Equal(e.GetProperty("Name").GetString(), EventNames.Prefix + e.GetProperty("Data").GetProperty("Event").GetString());
		}
	}

	[Fact]
	public void NoSampleContainsAPath()
	{
		string text = File.ReadAllText(NotificationFiles.RepoFile("docs", "reference", "notification-samples.json"));
		Assert.DoesNotContain(@":\\", text);
		Assert.DoesNotContain(@"\\\\", text);
		Assert.DoesNotContain("/mnt/", text);
	}

	[Fact]
	public void PlainJsonTurnsElementsIntoPlainValues()
	{
		JsonElement e = JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["A"] = 5, ["B"] = "x", ["C"] = true, ["D"] = new List<object?> { 1.5, null }, ["E"] = 12345678901L });
		Dictionary<string, object?> plain = Assert.IsType<Dictionary<string, object?>>(PlainJson.ToPlain(e));
		Assert.Equal(5L, plain["A"]);
		Assert.Equal("x", plain["B"]);
		Assert.Equal(true, plain["C"]);
		Assert.Equal(12345678901L, plain["E"]);
		List<object?> list = Assert.IsType<List<object?>>(plain["D"]);
		Assert.Equal(1.5, list[0]);
		Assert.Null(list[1]);
	}
}
