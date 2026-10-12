using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class RollUpTests
{
	private static readonly TagNames Names = new TagNames("explicit", "clean", "likely-clean", "explicit-review");

	[Fact]
	public void AnExplicitContainerGetsTheExplicitTagAtTheEnd()
	{
		TagChange c = TagPlan.ForRollUp(new[] { "rock", "90s" }, Names, true);
		Assert.True(c.Changed);
		Assert.Equal(new[] { "rock", "90s", "explicit" }, c.After);
	}

	[Fact]
	public void AContainerWithNoExplicitTrackLosesAnyManagedTag()
	{
		TagChange c = TagPlan.ForRollUp(new[] { "rock", "explicit", "clean" }, Names, false);
		Assert.True(c.Changed);
		Assert.Equal(new[] { "rock" }, c.After);
	}

	[Fact]
	public void AlreadyRightIsLeftAlone()
	{
		Assert.False(TagPlan.ForRollUp(new[] { "rock", "Explicit" }, Names, true).Changed);
		Assert.False(TagPlan.ForRollUp(new[] { "rock" }, Names, false).Changed);
		Assert.False(TagPlan.ForRollUp(new string[0], Names, false).Changed);
	}

	[Fact]
	public void AnotherManagedTagIsReplacedByExplicit()
	{
		TagChange c = TagPlan.ForRollUp(new[] { "clean", "x" }, Names, true);
		Assert.Equal(new[] { "x", "explicit" }, c.After);
	}
}
