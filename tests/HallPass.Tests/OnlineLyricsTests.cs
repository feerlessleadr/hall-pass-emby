using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class OnlineLyricsTests
{
	[Fact]
	public void PlainLyricsAreUsedWhenThereAre()
	{
		LyricsAnswer a = OnlineLyrics.Parse("{\"id\":1,\"duration\":200.5,\"instrumental\":false,\"plainLyrics\":\"one two\\nthree\",\"syncedLyrics\":\"[00:01.00] other\"}");
		Assert.True(a.Found);
		Assert.Equal("one two\nthree", a.Text);
		Assert.Equal(200.5, a.DurationSeconds);
	}

	[Fact]
	public void SyncedLyricsLoseTheirTimingMarks()
	{
		LyricsAnswer a = OnlineLyrics.Parse("{\"duration\":100,\"plainLyrics\":null,\"syncedLyrics\":\"[ar:Someone]\\n[00:01.50] hello there\\n[01:02.5] <01:03.00>again\"}");
		Assert.True(a.Found);
		Assert.Equal("hello there\nagain", a.Text.Replace("\r", string.Empty).Replace("  ", " ").Trim().Replace("\n ", "\n"));
	}

	[Fact]
	public void InstrumentalAndEmptyAnswersHaveNoText()
	{
		Assert.False(OnlineLyrics.Parse("{\"instrumental\":true,\"plainLyrics\":null}").Found);
		Assert.True(OnlineLyrics.Parse("{\"instrumental\":true}").Instrumental);
		Assert.False(OnlineLyrics.Parse("{\"plainLyrics\":\"  \"}").Found);
	}

	[Fact]
	public void ANotFoundAnswerAndGarbageAreTold()
	{
		Assert.False(OnlineLyrics.Parse("{\"statusCode\":404,\"name\":\"TrackNotFound\"}").Found);
		Assert.Null(OnlineLyrics.Parse("{\"statusCode\":404}").Message);
		Assert.NotNull(OnlineLyrics.Parse("<html>").Message);
		Assert.NotNull(OnlineLyrics.Parse(null).Message);
	}

	[Fact]
	public void BothVerdictsAreCappedAndMarkedOnline()
	{
		EvidenceItem strong = OnlineLyrics.Capped(new EvidenceItem("lyrics", Verdict.Explicit, 0.95, "score 12"));
		Assert.Equal(OnlineLyrics.Source, strong.Source);
		Assert.Equal(OnlineLyrics.ConfidenceCap, strong.Confidence);
		Assert.Equal(Verdict.Explicit, strong.Verdict);
		EvidenceItem weak = OnlineLyrics.Capped(new EvidenceItem("lyrics", Verdict.Clean, 0.5, "x"));
		Assert.Equal(0.5, weak.Confidence);
	}

	[Fact]
	public void LengthsMustAgreeWhenBothAreKnown()
	{
		Assert.True(OnlineLyrics.LengthsAgree(200, 203));
		Assert.False(OnlineLyrics.LengthsAgree(200, 260));
		Assert.True(OnlineLyrics.LengthsAgree(null, 260));
		Assert.True(OnlineLyrics.LengthsAgree(200, null));
	}

	[Fact]
	public void OnlineExplicitLyricsAloneOnlyReachReview()
	{
		EvidenceItem e = OnlineLyrics.Capped(new EvidenceItem("lyrics", Verdict.Explicit, 0.95, "score 30"));
		TrackRecord r = new TrackRecord { ItemId = 1, FileKey = "k", EvidenceVersion = "v", Evidence = { e } };
		DecisionResult d = DecisionEngine.Decide(ManualOverride.None, r.Evidence, DecisionSettings.Balanced);
		Assert.Equal(Decision.Review, d.Decision);
	}
}
