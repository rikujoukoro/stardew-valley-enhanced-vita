using System;
using System.IO;

namespace StardewValley;

/// <summary>Local preferences only; never part of a farm save or network message. Bits 0-2: overlays, bit 3: chest helper, bit 4: three extra ring slots.</summary>
internal static class VitaQoLSettings
{
	private static bool loaded;
	private static int flags;
	public static int Flags { get { Load(); return flags; } }
	/// <summary>The three overlay bits only; the chest helper (bit 3) draws nothing.</summary>
	public static int OverlayFlags => Flags & 7;
	public static bool Enabled(int feature) => (Flags & (1 << feature)) != 0;
	private static string ConfigPath => Path.Combine(Program.GetAppDataFolder(), "vita-qol.txt");
	private static void Load()
	{
		if (loaded) return;
		loaded = true;
		try { if (File.Exists(ConfigPath) && int.TryParse(File.ReadAllText(ConfigPath).Trim(), out int value)) flags = value & 31; }
		catch (Exception e) { Console.WriteLine("[Vita QoL] Preferences not read: " + e.Message); }
	}
	public static void Toggle(int feature)
	{
		if (feature < 0 || feature > 4) return;
		Load();
		flags ^= 1 << feature;
		if (feature == 4) VitaExtraRings.OnEnabledChanged();
		try
		{
			string path = ConfigPath;
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
			File.WriteAllText(path, flags.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}
		catch (Exception e) { Console.WriteLine("[Vita QoL] Preferences not saved: " + e.Message); }
	}
}
