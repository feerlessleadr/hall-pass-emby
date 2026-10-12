using System;
using System.Collections.Generic;
using System.Linq;

namespace HallPass.Core;

/// <summary>
/// Track ids waiting to be looked at after a library event. A bulk import raises thousands of events, so work starts only after things have been
/// quiet for a while (or the oldest id has waited long enough), and in small batches. Thread safe; the clock is injected for tests.
/// </summary>
public sealed class PendingQueue
{
	private readonly object _gate = new object();

	private readonly Dictionary<long, long> _addedAt = new Dictionary<long, long>();

	private readonly List<long> _order = new List<long>();

	private readonly Func<long> _nowMs;

	private long _lastAdd;

	public PendingQueue(Func<long>? nowMs = null)
	{
		_nowMs = nowMs ?? (() => Environment.TickCount64);
	}

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _order.Count;
			}
		}
	}

	/// <summary>Adds an id (a second add of the same id changes nothing except that things are not quiet yet).</summary>
	public void Add(long id)
	{
		lock (_gate)
		{
			long now = _nowMs();
			_lastAdd = now;
			if (!_addedAt.ContainsKey(id))
			{
				_addedAt[id] = now;
				_order.Add(id);
			}
		}
	}

	/// <summary>Puts ids back (for example when a job was busy); they wait for a fresh quiet period.</summary>
	public void Requeue(IEnumerable<long> ids)
	{
		foreach (long id in ids)
		{
			Add(id);
		}
	}

	/// <summary>Up to <paramref name="maxBatch"/> ids, oldest first, when nothing was added for <paramref name="quietMs"/> or the oldest has waited <paramref name="maxWaitMs"/>; otherwise none.</summary>
	public IReadOnlyList<long> TakeDue(long quietMs, long maxWaitMs, int maxBatch)
	{
		lock (_gate)
		{
			if (_order.Count == 0)
			{
				return Array.Empty<long>();
			}
			long now = _nowMs();
			bool quiet = now - _lastAdd >= quietMs;
			bool overdue = now - _addedAt[_order[0]] >= maxWaitMs;
			if (!quiet && !overdue)
			{
				return Array.Empty<long>();
			}
			List<long> batch = _order.Take(Math.Max(1, maxBatch)).ToList();
			foreach (long id in batch)
			{
				_order.Remove(id);
				_addedAt.Remove(id);
			}
			return batch;
		}
	}

	public void Clear()
	{
		lock (_gate)
		{
			_order.Clear();
			_addedAt.Clear();
		}
	}
}
