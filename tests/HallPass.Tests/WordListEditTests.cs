using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class WordListEditTests
{
	private static int Score(WordList list, string text) => new ProfanityScanner(list).Scan(text).Score;

	[Fact]
	public void AnAddedWordCountsAtItsTier()
	{
		WordList list = WordList.Default().WithEdits(new[] { "Zorp " }, null, null, null, null);
		Assert.Equal(WordList.SevereWeight, list.Words["zorp"]);
		Assert.True(Score(list, "what a zorp you are") >= WordList.SevereWeight);
		Assert.Equal(0, Score(WordList.Default(), "what a zorp you are"));
	}

	[Fact]
	public void AWordMovedToAnotherTierTakesTheNewOne()
	{
		WordList list = WordList.Default().WithEdits(null, null, new[] { "shit" }, null, null);
		Assert.Equal(WordList.MildWeight, list.Words["shit"]);
		list = WordList.Default().WithEdits(null, new[] { "damn" }, null, null, null);
		Assert.Equal(WordList.ModerateWeight, list.Words["damn"]);
	}

	[Fact]
	public void AnIgnoredWordNeverCountsNotEvenAsAStem()
	{
		WordList list = WordList.Default().WithEdits(null, null, null, null, new[] { "shit", "fuck", "fucking", "motherfuckers" });
		Assert.False(list.Words.ContainsKey("shit"));
		Assert.DoesNotContain("fuck", list.Stems.Keys);
		Assert.Equal(0, Score(list, "shit shit shit fuck fucking motherfuckers"));
	}

	[Fact]
	public void AnIgnoredWordWinsOverAnAddedOne()
	{
		WordList list = WordList.Default().WithEdits(new[] { "zorp" }, null, null, null, new[] { "zorp" });
		Assert.False(list.Words.ContainsKey("zorp"));
	}

	[Fact]
	public void AnExtraAllowedWordIsLeftAlone()
	{
		WordList list = WordList.Default().WithEdits(null, null, null, new[] { "Dickson" }, null);
		Assert.Contains("dickson", list.Allow);
		Assert.Equal(0, Score(list, "Mister Dickson came by"));
	}

	[Fact]
	public void BlanksDuplicatesAndLongEntriesAreDropped()
	{
		Assert.Equal(new[] { "a", "b" }, WordList.Clean(new[] { " A ", "", "a", "B", new string('x', 41) }).ToArray());
		Assert.Empty(WordList.Clean(null));
	}

	[Fact]
	public void NoEditsGiveTheSameList()
	{
		WordList plain = WordList.Default();
		WordList same = plain.WithEdits(null, null, null, null, null);
		Assert.Equal(plain.Words.OrderBy(p => p.Key), same.Words.OrderBy(p => p.Key));
		Assert.Equal(plain.Allow.OrderBy(s => s), same.Allow.OrderBy(s => s));
	}
}
