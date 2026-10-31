using System;

namespace StardewValley;

/// <summary>Prepare two short effects silently while loading, using the existing bounded FAudio read cache.</summary>
internal static class VitaAudioWarmup
{
	private static ICue[] pending;
	private static int startedAt;
	private static bool worldPrepared;
	private static bool titlePrepared;

	public static void Title()
	{
		if (!VitaPlatform.IsVita || !(Game1.soundBank is SoundBankWrapper)) return;
		worldPrepared = false;
		Prepare(Game1.soundBank, "drumkit6", "bigDeSelect");
	}

	public static void World()
	{
		if (!VitaPlatform.IsVita || worldPrepared || !(Game1.soundBank is SoundBankWrapper)) return;
		worldPrepared = true;
		Prepare(Game1.soundBank, "stoneStep", "dirtyHit");
	}

	private static void Prepare(ISoundBank bank, string first, string second)
	{
		Release();
		pending = new ICue[2];
		startedAt = Environment.TickCount;
		try
		{
			// GetCue prepares the initial waves; never Play or change volume preferences.
			pending[0] = bank.GetCue(first);
			pending[1] = bank.GetCue(second);
			Console.WriteLine("[Vita audio] Silent warmup: " + first + ", " + second);
		}
		catch (Exception e)
		{
			Console.WriteLine("[Vita audio] Warmup skipped: " + e.Message);
			Release();
		}
	}

	public static void Update()
	{
		// InitializeSounds may run on a worker: prepare/dispose only from the main update thread.
		if (!titlePrepared) { titlePrepared = true; Title(); }
		if (pending != null && unchecked(Environment.TickCount - startedAt) >= 6000) Release();
	}

	private static void Release()
	{
		ICue[] cues = pending;
		pending = null;
		if (cues == null) return;
		foreach (ICue cue in cues)
		{
			try { cue?.Dispose(); }
			catch (Exception e) { Console.WriteLine("[Vita audio] Warmup dispose: " + e.Message); }
		}
	}

	public static void Reset() { Release(); worldPrepared = false; }
}
