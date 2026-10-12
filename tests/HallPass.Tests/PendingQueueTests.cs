using System.Collections.Generic;
using System.Linq;
using HallPass.Core;
using Xunit;

namespace HallPass.Tests;

public class PendingQueueTests
{
	[Fact]
	public void NothingIsDueWhileEventsKeepArriving()
	{
		long now = 0;
		PendingQueue q = new PendingQueue(() => now);
		q.Add(1);
		now = 10_000;
		q.Add(2);
		now = 20_000; // only 10 s since the last add
		Assert.Empty(q.TakeDue(30_000, 300_000, 50));
		Assert.Equal(2, q.Count);
	}

	[Fact]
	public void AQuietPeriodReleasesEverythingOldestFirst()
	{
		long now = 0;
		PendingQueue q = new PendingQueue(() => now);
		q.Add(3);
		q.Add(1);
		q.Add(2);
		now = 30_000;
		Assert.Equal(new long[] { 3, 1, 2 }, q.TakeDue(30_000, 300_000, 50).ToArray());
		Assert.Equal(0, q.Count);
		Assert.Empty(q.TakeDue(30_000, 300_000, 50));
	}

	[Fact]
	public void ABusyStreamIsStillServedOnceTheOldestHasWaitedLongEnough()
	{
		long now = 0;
		PendingQueue q = new PendingQueue(() => now);
		q.Add(1);
		for (int i = 0; i < 40; i++)
		{
			now += 10_000;
			q.Add(100 + i); // never quiet for 30 s
		}
		Assert.Equal(400_000, now);
		IReadOnlyList<long> due = q.TakeDue(30_000, 300_000, 5);
		Assert.Equal(new long[] { 1, 100, 101, 102, 103 }, due.ToArray());
	}

	[Fact]
	public void BatchesAreCappedAndTheRestWaits()
	{
		long now = 0;
		PendingQueue q = new PendingQueue(() => now);
		foreach (long id in Enumerable.Range(1, 7))
		{
			q.Add(id);
		}
		now = 60_000;
		Assert.Equal(new long[] { 1, 2, 3 }, q.TakeDue(30_000, 300_000, 3).ToArray());
		Assert.Equal(new long[] { 4, 5, 6 }, q.TakeDue(30_000, 300_000, 3).ToArray());
		Assert.Equal(new long[] { 7 }, q.TakeDue(30_000, 300_000, 3).ToArray());
	}

	[Fact]
	public void TheSameIdIsKeptOnce()
	{
		long now = 0;
		PendingQueue q = new PendingQueue(() => now);
		q.Add(5);
		q.Add(5);
		q.Add(5);
		Assert.Equal(1, q.Count);
		now = 31_000;
		Assert.Equal(new long[] { 5 }, q.TakeDue(30_000, 300_000, 10).ToArray());
	}

	[Fact]
	public void PutBackIdsWaitForAFreshQuietPeriod()
	{
		long now = 0;
		PendingQueue q = new PendingQueue(() => now);
		q.Add(1);
		now = 31_000;
		IReadOnlyList<long> due = q.TakeDue(30_000, 300_000, 10);
		q.Requeue(due); // the job was busy
		Assert.Empty(q.TakeDue(30_000, 300_000, 10));
		now = 62_000;
		Assert.Equal(new long[] { 1 }, q.TakeDue(30_000, 300_000, 10).ToArray());
	}

	[Fact]
	public void ClearForgetsEverything()
	{
		PendingQueue q = new PendingQueue(() => 0);
		q.Add(1);
		q.Clear();
		Assert.Equal(0, q.Count);
	}
}
