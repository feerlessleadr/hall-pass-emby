using System;
using System.Collections.Generic;
using System.Linq;

namespace HallPass.Core;

/// <summary>
/// What one websocket subscription (HallPassEventsStart) has already been sent, and what to send next (docs/NOTIFICATIONS.md section 5).
/// Emby-free so it can be tested: the Emby listener only asks <see cref="Next"/> for each connection.
/// </summary>
public sealed class EventSubscription
{
	public const string NotAllowed = "Administrator login required.";

	private readonly long? _afterSeq;

	private bool _started;

	private bool _refused;

	private long _lastSent;

	private long _tick;

	/// <param name="afterSeq">The highest Seq the app has already seen (the optional third value of the start message), or null.</param>
	/// <param name="tick">The heartbeat counter when the subscription was made.</param>
	public EventSubscription(long? afterSeq, long tick)
	{
		_afterSeq = afterSeq;
		_tick = tick;
	}

	public long LastSent => _lastSent;

	/// <summary>
	/// The next message body for this connection, or null when there is nothing to say. The first call answers the subscription (the events
	/// after <c>afterSeq</c>, or just <c>LatestSeq</c> as a baseline); later calls send events not yet sent, and an empty list as a heartbeat once per tick.
	/// A non-administrator gets the error once and nothing more.
	/// </summary>
	public Dictionary<string, object?>? Next(EventLog log, bool isAdministrator, long currentTick)
	{
		if (!isAdministrator)
		{
			if (_refused)
			{
				return null;
			}
			_refused = true;
			return new Dictionary<string, object?> { ["Error"] = NotAllowed };
		}
		bool truncated = false;
		IReadOnlyList<NotificationEvent> events;
		if (!_started)
		{
			_started = true;
			_tick = currentTick;
			if (_afterSeq.HasValue)
			{
				events = log.After(Math.Max(0, _afterSeq.Value), 100, out truncated);
			}
			else
			{
				events = Array.Empty<NotificationEvent>();
				_lastSent = log.LatestSeq;
			}
		}
		else
		{
			events = log.After(_lastSent, 100, out _);
			if (events.Count == 0)
			{
				if (_tick == currentTick)
				{
					return null;
				}
				_tick = currentTick;
			}
		}
		if (events.Count > 0)
		{
			_lastSent = events[events.Count - 1].Seq;
		}
		else if (_afterSeq.HasValue && _lastSent < _afterSeq.Value)
		{
			_lastSent = _afterSeq.Value;
		}
		return new Dictionary<string, object?>
		{
			["ApiLevel"] = EventNames.ApiLevel,
			["LatestSeq"] = log.LatestSeq,
			["Truncated"] = truncated,
			["Events"] = events.Select(ToPlain).ToList(),
		};
	}

	/// <summary>One event in the shape of the catch-up route: Seq, Name, AtUtc, Data.</summary>
	public static object ToPlain(NotificationEvent e) => new Dictionary<string, object?>
	{
		["Seq"] = e.Seq,
		["Name"] = e.Name,
		["AtUtc"] = e.AtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
		["Data"] = PlainJson.ToPlain(e.Data),
	};
}
