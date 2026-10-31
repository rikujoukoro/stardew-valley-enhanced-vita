namespace StardewValley.SDKs;

/// <summary>Port: the single-player build's platform SDK. No networking or overlay; achievements become Vita-style trophies (<see cref="T:StardewValley.SDKs.VitaTrophies" />).</summary>
public class VitaSDKHelper : SDKHelper
{
	public bool IsEnterButtonAssignmentFlipped => false;

	public bool IsJapaneseRegionRelease => false;

	public string Name => "Vita";

	public SDKNetHelper Networking => null;

	public bool ConnectionFinished => true;

	public int ConnectionProgress => 0;

	public bool HasOverlay => false;

	public void EarlyInitialize()
	{
	}

	public void Initialize()
	{
		VitaTrophies.Initialize();
	}

	/// <summary>True, as on Steam: loading a save reports the achievements it already has, which syncs the trophies.</summary>
	public bool RetroactiveAchievementsAllowed()
	{
		return true;
	}

	public void GetAchievement(string achieve)
	{
		VitaTrophies.Unlock(achieve);
	}

	public void ResetAchievements()
	{
		VitaTrophies.Reset();
	}

	public void Update()
	{
		VitaTrophies.Update();
	}

	public void Shutdown()
	{
	}

	public void DebugInfo()
	{
		Game1.log.Info("SDK: Vita trophies");
	}

	public string FilterDirtyWords(string words)
	{
		return words;
	}
}
