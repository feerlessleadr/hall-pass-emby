using System;
using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class TagPlanTests
{
	private static readonly TagNames Names = new TagNames("explicit", "clean", "likely-clean", "explicit-review");

	private static TagChange Plan(string[] current, Decision d, bool skippedAsClean = false) => TagPlan.ForDecision(current, Names, d, skippedAsClean);

	[Theory]
	[InlineData(Decision.Explicit, "explicit")]
	[InlineData(Decision.Clean, "clean")]
	[InlineData(Decision.LikelyClean, "likely-clean")]
	[InlineData(Decision.Review, "explicit-review")]
	public void EachDecisionGetsItsOwnTag(Decision decision, string tag)
	{
		TagChange c = Plan(new[] { "Rock" }, decision);
		Assert.True(c.Changed);
		Assert.Equal(new[] { "Rock", tag }, c.After);
		Assert.Equal(new[] { "Rock" }, c.Before);
	}

	[Fact]
	public void OtherManagedTagsAreTakenOffSoTheyNeverCoexist()
	{
		TagChange c = Plan(new[] { "Rock", "explicit", "likely-clean", "Party" }, Decision.Clean);
		Assert.True(c.Changed);
		Assert.Equal(new[] { "Rock", "Party", "clean" }, c.After);
	}

	[Fact]
	public void NoChangeWhenTheRightTagIsAlreadyTheOnlyManagedOne()
	{
		TagChange same = Plan(new[] { "Rock", "likely-clean" }, Decision.LikelyClean);
		Assert.False(same.Changed);
		Assert.Equal(same.Before, same.After);
		// any letter case counts as the right tag
		Assert.False(Plan(new[] { "Likely-Clean" }, Decision.LikelyClean).Changed);
		// the right tag plus a wrong one is a change
		Assert.True(Plan(new[] { "likely-clean", "explicit" }, Decision.LikelyClean).Changed);
	}

	[Fact]
	public void SkippedTracksLoseOurTagsOrGetCleanWhenAsked()
	{
		TagChange gone = Plan(new[] { "Classical", "explicit-review" }, Decision.Skipped);
		Assert.True(gone.Changed);
		Assert.Equal(new[] { "Classical" }, gone.After);
		Assert.False(Plan(new[] { "Classical" }, Decision.Skipped).Changed);
		Assert.Equal(new[] { "Classical", "clean" }, Plan(new[] { "Classical" }, Decision.Skipped, skippedAsClean: true).After);
	}

	[Fact]
	public void UnscannedTracksAreNeverTouched()
	{
		TagChange c = Plan(new[] { "explicit", "x" }, Decision.Unscanned);
		Assert.False(c.Changed);
		Assert.Equal(new[] { "explicit", "x" }, c.After);
	}

	[Fact]
	public void OtherTagsKeepTheirOrderCaseAndDuplicates()
	{
		TagChange c = Plan(new[] { "b", "A", "b" }, Decision.Explicit);
		Assert.Equal(new[] { "b", "A", "b", "explicit" }, c.After);
	}

	[Fact]
	public void RunningThePlanTwiceChangesNothingTheSecondTime()
	{
		TagChange first = Plan(new[] { "Rock" }, Decision.Review);
		Assert.False(Plan(first.After, Decision.Review).Changed);
	}

	[Fact]
	public void TagNamesKnowWhichAreTheirs()
	{
		Assert.True(Names.IsManaged("EXPLICIT"));
		Assert.False(Names.IsManaged("explicit lyrics"));
		Assert.Equal(4, Names.All.Count);
	}
}

public class JournalTests
{
	private static JournalEntry Entry(string[] before, string[] after, bool lockAdded = false) => new JournalEntry { ItemId = 7, Name = "Song", Before = before, After = after, LockAdded = lockAdded };

	[Fact]
	public void LinesRoundTrip()
	{
		JournalHeader header = new JournalHeader { JobId = "abc", Kind = "apply", StartedUtc = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc) };
		JournalHeader? back = JournalFormat.ReadHeader(JournalFormat.ToLine(header));
		Assert.NotNull(back);
		Assert.Equal("abc", back!.JobId);
		Assert.Equal(header.StartedUtc, back.StartedUtc);

		JournalEntry? entry = JournalFormat.ReadEntry(JournalFormat.ToLine(Entry(new[] { "Rock" }, new[] { "Rock", "clean" }, true)));
		Assert.NotNull(entry);
		Assert.Equal(new[] { "Rock", "clean" }, entry!.After);
		Assert.True(entry.LockAdded);

		JournalTrailer? trailer = JournalFormat.ReadTrailer(JournalFormat.ToLine(new JournalTrailer { Changed = 5, Failed = 1, Cancelled = true }));
		Assert.NotNull(trailer);
		Assert.Equal(5, trailer!.Changed);
		Assert.True(trailer.Cancelled);
	}

	[Fact]
	public void EachReaderAcceptsOnlyItsOwnLineType()
	{
		string entryLine = JournalFormat.ToLine(Entry(new string[0], new[] { "x" }));
		Assert.Null(JournalFormat.ReadHeader(entryLine));
		Assert.Null(JournalFormat.ReadTrailer(entryLine));
		Assert.NotNull(JournalFormat.ReadEntry(entryLine));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not json")]
	[InlineData("[1,2]")]
	[InlineData("{\"Type\":\"other\"}")]
	public void DamagedOrForeignLinesAreIgnored(string? line)
	{
		Assert.Null(JournalFormat.ReadHeader(line));
		Assert.Null(JournalFormat.ReadEntry(line));
		Assert.Null(JournalFormat.ReadTrailer(line));
	}

	[Fact]
	public void UndoRestoresOnlyWhenTheTagsAreStillWhatTheJobLeft()
	{
		JournalEntry e = Entry(new[] { "Rock" }, new[] { "Rock", "clean" });
		Assert.Equal(UndoAction.Restore, JournalFormat.DecideUndo(new[] { "Rock", "clean" }, e, false));
		Assert.Equal(UndoAction.Restore, JournalFormat.DecideUndo(new[] { "clean", "ROCK" }, e, false)); // order and case do not matter
		Assert.Equal(UndoAction.AlreadyRestored, JournalFormat.DecideUndo(new[] { "Rock" }, e, false));
	}

	[Fact]
	public void ATrackEditedSinceIsAConflictUnlessForced()
	{
		JournalEntry e = Entry(new[] { "Rock" }, new[] { "Rock", "clean" });
		string[] edited = { "Rock", "clean", "Party" };
		Assert.Equal(UndoAction.Conflict, JournalFormat.DecideUndo(edited, e, false));
		Assert.Equal(UndoAction.Restore, JournalFormat.DecideUndo(edited, e, true));
		// already restored wins even when forced: nothing to do
		Assert.Equal(UndoAction.AlreadyRestored, JournalFormat.DecideUndo(new[] { "Rock" }, e, true));
	}

	[Fact]
	public void TagSetsCompareWithoutOrderOrCase()
	{
		Assert.True(JournalFormat.SameTags(new[] { "a", "B" }, new[] { "b", "A" }));
		Assert.False(JournalFormat.SameTags(new[] { "a" }, new[] { "a", "b" }));
		Assert.True(JournalFormat.SameTags(new string[0], new string[0]));
	}

	[Fact]
	public void RetentionKeepsTheNewestJobsAndAnythingRecent()
	{
		DateTime now = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
		List<(string, DateTime)> jobs = Enumerable.Range(0, 30).Select(i => ("job" + i, now.AddDays(-i * 5))).ToList();
		IReadOnlyList<string> doomed = JournalFormat.JobsToDelete(jobs, 25, 30, now);
		// jobs 25..29 are beyond the newest 25 and older than 30 days
		Assert.Equal(new[] { "job25", "job26", "job27", "job28", "job29" }.OrderBy(s => s), doomed.OrderBy(s => s));
		// a job beyond the newest N that is still young survives
		List<(string, DateTime)> young = Enumerable.Range(0, 5).Select(i => ("y" + i, now.AddHours(-i))).ToList();
		Assert.Empty(JournalFormat.JobsToDelete(young, 2, 30, now));
		Assert.Empty(JournalFormat.JobsToDelete(new List<(string, DateTime)>(), 25, 30, now));
	}
}
