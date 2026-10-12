namespace HallPass.Services;

/// <summary>Only one job (scan, apply or undo) runs at a time.</summary>
internal static class JobGate
{
	private static readonly object Lock = new object();

	private static string? _running;

	public static string? Running
	{
		get
		{
			lock (Lock)
			{
				return _running;
			}
		}
	}

	public static bool TryEnter(string name)
	{
		lock (Lock)
		{
			if (_running != null)
			{
				return false;
			}
			_running = name;
			return true;
		}
	}

	public static void Exit()
	{
		lock (Lock)
		{
			_running = null;
		}
	}
}
