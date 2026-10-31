using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SDL3;

namespace StardewValley;

/// <summary>Port: what differs when the game runs on the PS Vita (Mono + FNA, launched by Tools/Vita/stardew).</summary>
public static class VitaPlatform
{
	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern int vita_port_request_restart();
	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	internal static extern void vita_port_cancel_ime();
	public static bool TryRestartGame()
	{
		if (!IsVita || GameRunner.instance == null) return false;
		try
		{
			if (vita_port_request_restart() != 0) return false;
			KeyboardInput.SetTextInputActive(false);
			GameRunner.instance.Exit(); // Return to launcher so JIT memory is released before LoadExec.
			return true;
		}
		catch (Exception error) { Trace("restart request failed: " + error.Message); return false; }
	}
	/// <summary>The Vita's only display mode; the game always runs fullscreen at this size.</summary>
	public const int ScreenWidth = 960;

	public const int ScreenHeight = 544;

	/// <summary>World and UI zoom on top of the player's settings, as split-screen uses it.</summary>
	/// <remarks>
	/// The UI is laid out for at least 1280x720: at 960x544 menus run off screen (character creation lost
	/// its OK button). 0.75 gives the Vita the layout of a 1280x725 window, and since the game draws
	/// its art at 4x, it lands on exactly 3 screen pixels per art pixel.
	/// </remarks>
	public const float ZoomModifier = 0.75f;

	/// <summary>The zoom level setting the Vita always uses (on top of <see cref="ZoomModifier" />); the options page doesn't offer it.</summary>
	/// <remarks>
	/// The world and the UI are drawn at 4 texels per art pixel into buffers that are scaled onto the screen,
	/// so only scales giving whole screen pixels per art pixel draw evenly: 105% zoom and 90% UI (3.15 and
	/// 2.7 pixels) left lines in the art that moved with the camera. The toolbar overlapping the clock is
	/// fixed by pinning the toolbar instead (<see cref="M:StardewValley.Options.platformClampValues" />).
	/// </remarks>
	/// <remarks>The title screen always uses 100%: its sky, clouds and hills are layers covering the whole buffer, and at 67% (2.25 times the pixels) the GPU managed 19 fps there.</remarks>
	public static float BaseZoomLevel => (Game1.gameMode == Game1.titleScreenGameMode) ? 1f : CameraZoomLevels[CameraZoom];

	/// <summary>The options page's camera zoom choice (an id the game's own options don't use).</summary>
	public const int CloseZoomOption = 1000;

	/// <summary>The world zooms the camera choice offers, with <see cref="ZoomModifier" />: Uzak (Far) = 2 screen pixels per art pixel (67%) and Yakın (Close) = 3 (100%). Both put every art pixel on whole screen pixels, so they draw the art without the faint lines of uneven pixel widths, and both draw the world straight to the screen (<see cref="F:StardewValley.Game1.VitaDirectWorldScale" />). The in-between levels (88%, 117%) and 133% were dropped because they were slower than these on the Vita. Closer means less world to draw each frame. The UI keeps its scale.</summary>
	public static readonly float[] CameraZoomLevels = new float[2] { 2f / 3f, 1f };

	/// <summary>The choices as the options page lists them, in the current language (Uzak/Yakın in Turkish, Far/Close otherwise); evaluated each time, since the language can change at runtime.</summary>
	public static System.Collections.Generic.List<string> CameraZoomLabels => new System.Collections.Generic.List<string> { IsTr ? "Uzak" : "Far", IsTr ? "Yakın" : "Close" };

	/// <summary>The index of a displayed label (in either language, so a page built before a language change still maps), or -1.</summary>
	public static int CameraZoomIndexOf(string label)
	{
		switch (label)
		{
		case "Uzak":
		case "Far":
			return 0;
		case "Yakın":
		case "Close":
			return 1;
		default:
			return -1;
		}
	}

	/// <summary>The choice without the file: Yakın (Close, 100%).</summary>
	private const int DefaultCameraZoom = 1;

	/// <summary>Language-independent names of the choices, as kept in the file and written to the log.</summary>
	private static readonly string CameraZoomFar = "far";

	private static readonly string CameraZoomNear = "near";

	/// <summary>The fixed ASCII name of the current choice (the same string instance each time).</summary>
	public static string CameraZoomToken => (CameraZoom == 0) ? CameraZoomFar : CameraZoomNear;

	/// <summary>Where the camera choice is kept: its own file next to the game holding "far" or "near" ("67%" and "88%" from earlier builds mean far; "100%", "117%", "133%", "on", "1", "2" and anything else mean near), since the options file is written by the pregenerated serializer.</summary>
	private const string CloseZoomFile = "vita-close-zoom";

	private static int _cameraZoom = -1;

	/// <summary>The camera choice: an index into <see cref="CameraZoomLevels" />, from <see cref="CloseZoomFile" /> in the game folder.</summary>
	public static int CameraZoom
	{
		get
		{
			if (_cameraZoom < 0)
			{
				_cameraZoom = DefaultCameraZoom;
				try
				{
					if (IsVita && System.IO.File.Exists(CloseZoomFile))
					{
						string saved = System.IO.File.ReadAllText(CloseZoomFile).Trim().ToLowerInvariant();
						_cameraZoom = (saved == "far" || saved == "67%" || saved == "88%") ? 0 : 1;
					}
				}
				catch (Exception)
				{
				}
			}
			return _cameraZoom;
		}
		set
		{
			_cameraZoom = Math.Max(0, Math.Min(CameraZoomLevels.Length - 1, value));
			try
			{
				if (_cameraZoom != DefaultCameraZoom)
				{
					System.IO.File.WriteAllText(CloseZoomFile, CameraZoomFar);
				}
				else if (System.IO.File.Exists(CloseZoomFile))
				{
					System.IO.File.Delete(CloseZoomFile);
				}
			}
			catch (Exception ex)
			{
				Trace("camera zoom setting not saved: " + ex.Message);
			}
		}
	}

	public const int FpsLimitOption = 1003;

	/// <summary>The frame rate cap choices; 60 is the display's own rate (no cap); the last is automatic (60 while a frame fits in 16.6 ms, else 30, as the official Vita release did).</summary>
	/// <remarks>45 is gone: on a 60 Hz screen it can only show three frames of every four, an uneven step by design. 30 shows
	/// every frame for exactly two refreshes (the display waits two vblanks per flip, GameRunner.BeginDraw); it used to skip
	/// draws by the clock instead, which drifted against the vsync (28 fps, uneven).</remarks>
	public static readonly System.Collections.Generic.List<string> FpsLimitLabels = new System.Collections.Generic.List<string> { "30", "60", "Auto" };

	/// <summary>0 for the automatic choice (GameRunner picks 60 or 30).</summary>
	private static readonly int[] FpsLimits = new int[3] { 30, 60, 0 };

	/// <summary>Whether the cap is the automatic one.</summary>
	public static bool FpsLimitAuto => IsVita && FpsLimits[FpsLimitIndex] == 0;

	private const string FpsLimitFile = "vita-fps-limit";

	private static int _fpsLimit = -1;

	public static string FpsLimitLabel => IsTr ? "Kare hızı sınırı" : "Frame rate cap";

	/// <summary>The cap choice: an index into <see cref="FpsLimitLabels" />, kept in vita-fps-limit as its label (no file, or the old "45": 60).</summary>
	public static int FpsLimitIndex
	{
		get
		{
			if (_fpsLimit < 0)
			{
				_fpsLimit = NoCapIndex;
				try
				{
					if (IsVita && System.IO.File.Exists(FpsLimitFile))
					{
						int index = FpsLimitLabels.IndexOf(System.IO.File.ReadAllText(FpsLimitFile).Trim());
						if (index >= 0)
						{
							_fpsLimit = index;
						}
					}
				}
				catch (Exception)
				{
				}
			}
			return _fpsLimit;
		}
		set
		{
			_fpsLimit = Math.Max(0, Math.Min(FpsLimits.Length - 1, value));
			try
			{
				if (_fpsLimit != NoCapIndex)
				{
					System.IO.File.WriteAllText(FpsLimitFile, FpsLimitLabels[_fpsLimit]);
				}
				else if (System.IO.File.Exists(FpsLimitFile))
				{
					System.IO.File.Delete(FpsLimitFile);
				}
			}
			catch (Exception ex)
			{
				Trace("frame rate cap not saved: " + ex.Message);
			}
		}
	}

	/// <summary>The "60" choice: no cap, and the default without the file.</summary>
	private const int NoCapIndex = 1;

	/// <summary>Frames per second the game draws at most (updates stay at 60 a second); 0 for the automatic choice.</summary>
	public static int FpsLimit => IsVita ? FpsLimits[FpsLimitIndex] : 60;

	public const int ShakeOption = 1001;

	public const int FewParticlesOption = 1002;

	private const string NoShakeFile = "vita-no-shake";

	private const string FewParticlesFile = "vita-few-particles";

	/// <summary>Set by the options page; Game1.Draw saves the presented frame then clears it.</summary>
	public static bool ScreenshotRequested;

	private static int _shake = -1;

	private static int _few = -1;

	/// <summary>Bush/grass/tree wobble animations; on by default, off when vita-no-shake exists. Gameplay effects of shaking are unaffected.</summary>
	public static bool ShakeEnabled
	{
		get
		{
			if (!IsVita)
			{
				return true;
			}
			if (_shake < 0)
			{
				try
				{
					_shake = System.IO.File.Exists(NoShakeFile) ? 0 : 1;
				}
				catch (Exception)
				{
					_shake = 1;
				}
			}
			return _shake == 1;
		}
		set
		{
			_shake = value ? 1 : 0;
			SetFlagFile(NoShakeFile, !value);
		}
	}

	/// <summary>Fewer purely cosmetic particles; off by default, on when vita-few-particles exists.</summary>
	public static bool FewParticles
	{
		get
		{
			if (!IsVita)
			{
				return false;
			}
			if (_few < 0)
			{
				try
				{
					_few = System.IO.File.Exists(FewParticlesFile) ? 1 : 0;
				}
				catch (Exception)
				{
					_few = 0;
				}
			}
			return _few == 1;
		}
		set
		{
			_few = value ? 1 : 0;
			SetFlagFile(FewParticlesFile, value);
		}
	}

	public const int MinimalDebrisOption = 1006;

	private const string FullDebrisFile = "vita-full-debris";

	private static int _minimalDebris = -1;

	/// <summary>Port: merges multi-item drops (tree wood/sap/seeds, coffee/berry multi-harvests, multi-object debris) into a single stacked drop and trims chop debris; on by default on Vita, off when vita-full-debris exists.</summary>
	public static bool MinimalDebris
	{
		get
		{
			if (!IsVita)
			{
				return false;
			}
			if (_minimalDebris < 0)
			{
				try
				{
					_minimalDebris = System.IO.File.Exists(FullDebrisFile) ? 0 : 1;
				}
				catch (Exception)
				{
					_minimalDebris = 1;
				}
			}
			return _minimalDebris == 1;
		}
		set
		{
			_minimalDebris = value ? 1 : 0;
			SetFlagFile(FullDebrisFile, !value);
		}
	}

	public const int AutoAttackOption = 1004;

	public const int AutoToolOption = 1005;

	private const string AutoAttackFile = "vita-auto-attack";

	private const string AutoToolFile = "vita-auto-tool";

	private static int _autoAttack = -1;

	private static int _autoTool = -1;

	/// <summary>Port: swing the best melee weapon at monsters next to the player (see <see cref="VitaAutoActions" />); off by default, on when vita-auto-attack exists.</summary>
	public static bool AutoAttack
	{
		get
		{
			if (_autoAttack < 0)
			{
				try
				{
					_autoAttack = System.IO.File.Exists(AutoAttackFile) ? 1 : 0;
				}
				catch (Exception)
				{
					_autoAttack = 0;
				}
			}
			return _autoAttack == 1;
		}
		set
		{
			_autoAttack = value ? 1 : 0;
			SetFlagFile(AutoAttackFile, value);
		}
	}

	/// <summary>Port: pick the tool that fits the target tile when the tool button is pressed; off by default, on when vita-auto-tool exists.</summary>
	public static bool AutoTool
	{
		get
		{
			if (_autoTool < 0)
			{
				try
				{
					_autoTool = System.IO.File.Exists(AutoToolFile) ? 1 : 0;
				}
				catch (Exception)
				{
					_autoTool = 0;
				}
			}
			return _autoTool == 1;
		}
		set
		{
			_autoTool = value ? 1 : 0;
			SetFlagFile(AutoToolFile, value);
		}
	}

	public static string AutoAttackLabel => IsTr ? "Otomatik saldırı" : "Auto attack";

	public static string AutoToolLabel => IsTr ? "Otomatik alet seçimi" : "Auto tool select";

	private static void SetFlagFile(string file, bool present)
	{
		try
		{
			if (present)
			{
				System.IO.File.WriteAllText(file, "on");
			}
			else if (System.IO.File.Exists(file))
			{
				System.IO.File.Delete(file);
			}
		}
		catch (Exception ex)
		{
			Trace("setting not saved: " + ex.Message);
		}
	}

	internal static bool IsTr => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.tr;

	public static string GraphicsHeaderLabel => IsTr ? "Grafik" : "Graphics";

	public static string NoShakeLabel => IsTr ? "Çalı/çimen/ağaç sallanması" : "Bush/grass/tree shaking";

	public static string FewParticlesLabel => IsTr ? "Daha az parçacık" : "Fewer particles";

	public static string MinimalDebrisLabel => IsTr ? "Düşen eşyaları tek parçada birleştir" : "Merge dropped items";

	/// <summary>The option's label, in the game's language where there is one.</summary>
	public static string CloseZoomLabel => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.tr ? "Kamera yakınlığı" : "Camera zoom";

	/// <summary>The UI scale setting the Vita always uses (on top of <see cref="ZoomModifier" />), on the title screen too, so the game's render targets are sized once at startup. See <see cref="BaseZoomLevel" />.</summary>
	public const float BaseUIScale = 1f;

	/// <summary>Whether the game runs on the Vita, from SDL's platform name.</summary>
	public static readonly bool IsVita = Detect();

	/// <summary>Whether the fine-grained timing (per location, per NPC: <see cref="BeginHotSection" />, the NPC slow traces) runs: a file or folder named profile-sections in the game folder.</summary>
	/// <remarks>Several hundred clock reads a tick cost 4-6% of the main thread (profile-samples), so they are off unless asked for. The fps lines and spike lines stay.</remarks>
	public static readonly bool ProfileSections = IsVita && DetectSwitch("profile-sections");

	/// <summary>Whether the 67% and 100% cameras draw the world through the screen buffer as the other zooms do (a file or folder named vita-buffered-camera in the game folder), instead of straight to the screen (Game1.VitaDirectWorldScale): to compare the two.</summary>
	public static readonly bool BufferedCamera = IsVita && DetectSwitch("vita-buffered-camera");

	/// <summary>Whether the back layers are drawn tile by tile every frame as before (a file or folder named vita-tile-back-layers in the game folder), instead of from the picture Game1 keeps of them: to compare the two.</summary>
	public static readonly bool TileBackLayers = IsVita && DetectSwitch("vita-tile-back-layers");

	/// <summary>Whether paths and floors are drawn sprite by sprite every frame as before (a file or folder named vita-live-floors in the game folder), instead of from the picture Game1 keeps of them: to compare the two.</summary>
	public static readonly bool LiveFloors = IsVita && DetectSwitch("vita-live-floors");

#if VITA_RELEASE
	/// <summary>Public builds (vita-builder, -p:VitaRelease=true): no test menu at all; every use of it is dead code.</summary>
	public const bool DebugMenu = false;
#else
	/// <summary>A file named vita-debug in the game folder: L + R + SELECT opens the test menu (<see cref="VitaDebugMenu" />).</summary>
	public static readonly bool DebugMenu = IsVita && DetectSwitch("vita-debug");
#endif

	/// <summary>The list <see cref="PrepareHotMethods" /> compiles: "Namespace.Type:Method" lines, nested types as "Outer/Inner", in the game folder.</summary>
	/// <remarks>Built from a jit-log session (Tools/Vita/make-prejit-list.sh): the methods compiled in the first minutes of play.</remarks>
	private const string PreJitListFile = "vita-prejit.txt";

	private static bool _preJitDone;

	/// <summary>Compiles the methods in <see cref="PreJitListFile" /> ahead of play, once a session; yields progress (0-100) between batches so the loading screen keeps drawing.</summary>
	/// <remarks>
	/// The game's per-frame code stays JIT-compiled (AOT code ran it about twice as slow), so the first
	/// walk, tool swing or menu of a session compiled hundreds of methods at once: ~1,600 methods and
	/// 3.5 s in the first minute after loading, as 100-700 ms stalls. Compiling them here takes as long
	/// but happens behind the loading screen. Methods already compiled (or AOT-compiled) cost nothing.
	/// (Until 2026-10-24 this called RuntimeHelpers.PrepareMethod, which Mono doesn't implement: the
	/// trace's "N methods compiled" counted calls, and nothing was compiled.)
	/// </remarks>
	public static System.Collections.Generic.IEnumerable<int> PrepareHotMethods()
	{
		if (_preJitInBackground)
		{
			// Compiled (or still compiling) on its own thread since the title screen.
			return System.Linq.Enumerable.Empty<int>();
		}
		return CompileHotMethods();
	}

	private static volatile bool _preJitInBackground;

	/// <summary>Compiles <see cref="PreJitListFile" /> on a thread of its own from the title screen on, so it takes nothing from the loading screen (it took 10 s there once it really compiled); the Vita gives a game three cores and the main thread uses one. A method the game reaches first is compiled by the game as before.</summary>
	public static void StartBackgroundPreJit()
	{
		if (!IsVita || _preJitDone || _preJitInBackground)
		{
			return;
		}
		_preJitInBackground = true;
		System.Threading.Thread thread = new System.Threading.Thread(() =>
		{
			try
			{
				foreach (int step in CompileHotMethods())
				{
				}
			}
			catch (Exception ex)
			{
				Trace("pre-JIT stopped: " + ex.Message);
			}
		});
		thread.IsBackground = true;
		thread.Name = "pre-JIT";
		try
		{
			thread.Priority = System.Threading.ThreadPriority.BelowNormal;
		}
		catch (Exception)
		{
		}
		thread.Start();
	}

	private static System.Collections.Generic.IEnumerable<int> CompileHotMethods()
	{
		if (!IsVita || _preJitDone)
		{
			yield break;
		}
		_preJitDone = true;
		string[] lines = null;
		try
		{
			lines = System.IO.File.ReadAllLines(PreJitListFile);
		}
		catch (Exception)
		{
		}
		if (lines == null)
		{
			yield break;
		}
		System.Reflection.Assembly[] assemblies = new System.Reflection.Assembly[3]
		{
			typeof(Game1).Assembly,
			typeof(Microsoft.Xna.Framework.Game).Assembly,
			typeof(xTile.Map).Assembly
		};
		const System.Reflection.BindingFlags members = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
		System.Collections.Generic.Dictionary<string, System.Reflection.MethodBase[]> typeMembers = new System.Collections.Generic.Dictionary<string, System.Reflection.MethodBase[]>();
		double start = Clock.Elapsed.TotalMilliseconds;
		int prepared = 0;
		int missing = 0;
		int genericEntries = 0;
		int genericMissing = 0;
		PreJitResolver genericResolver = null;
		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i];
			int colon = line.IndexOf(':');
			if (colon > 0 && line.StartsWith("(wrapper", StringComparison.Ordinal))
			{
				colon = -1;
			}
			if (colon > 0 && PreJitResolver.IsGenericEntry(line.Substring(0, colon), line.Substring(colon + 1).Trim()))
			{
				// Closed generic types / generic methods in the jit log's format (Dictionary`2<Point, bool>:TryInsert).
				if (genericResolver == null)
				{
					genericResolver = new PreJitResolver();
				}
				genericEntries++;
				int compiledNow = genericResolver.Compile(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim(), out bool resolvedNow);
				prepared += compiledNow;
				if (!resolvedNow)
				{
					genericMissing++;
				}
			}
			else if (colon > 0)
			{
				string typeName = line.Substring(0, colon).Replace('/', '+');
				string methodName = line.Substring(colon + 1).Trim();
				if (!typeMembers.TryGetValue(typeName, out System.Reflection.MethodBase[] candidates))
				{
					candidates = null;
					try
					{
						foreach (System.Reflection.Assembly assembly in assemblies)
						{
							Type type = assembly.GetType(typeName, throwOnError: false);
							if (type != null)
							{
								if (!type.ContainsGenericParameters)
								{
									System.Collections.Generic.List<System.Reflection.MethodBase> found = new System.Collections.Generic.List<System.Reflection.MethodBase>();
									found.AddRange(type.GetMethods(members));
									found.AddRange(type.GetConstructors(members));
									candidates = found.ToArray();
								}
								break;
							}
						}
					}
					catch (Exception)
					{
						candidates = null;
					}
					typeMembers[typeName] = candidates;
				}
				bool any = false;
				if (candidates != null)
				{
					foreach (System.Reflection.MethodBase method in candidates)
					{
						if (method.Name != methodName || method.IsAbstract || method.ContainsGenericParameters || method.IsStatic && method.IsConstructor)
						{
							continue;
						}
						try
						{
							// Not RuntimeHelpers.PrepareMethod: Mono's is empty ("FIXME: Implement"), it compiled
							// nothing. GetFunctionPointer has the method compiled to give its address.
							method.MethodHandle.GetFunctionPointer();
							prepared++;
							any = true;
						}
						catch (Exception)
						{
						}
					}
				}
				if (!any)
				{
					missing++;
				}
			}
			if (i % 40 == 39)
			{
				yield return i * 100 / lines.Length;
			}
		}
		Trace($"pre-JIT: {prepared} methods compiled in {Clock.Elapsed.TotalMilliseconds - start:F0} ms ({missing} of {lines.Length} list entries not found; generic entries {genericEntries - genericMissing}/{genericEntries} resolved)");
	}

	private static bool DetectSwitch(string name)
	{
		try
		{
			return System.IO.File.Exists(name) || System.IO.Directory.Exists(name);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Touch screen corner taps since the last call: bit 0 = bottom-left, bit 1 = bottom-right (Tools/Vita/stardew/main.c; the native name is from when the rear panel did this).</summary>
	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern int vita_port_rear_touch_taps();

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern int vita_port_front_touch(out int x, out int y);

	private static bool _frontTouchSupported = true;

	public static bool ReadFrontTouch(out int x, out int y)
	{
		if (IsVita && _frontTouchSupported)
		{
			try
			{
				return vita_port_front_touch(out x, out y) != 0;
			}
			catch (EntryPointNotFoundException)
			{
				_frontTouchSupported = false;
			}
		}
		x = 0;
		y = 0;
		return false;
	}

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern int vita_port_gl_free_kb();

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern void vita_port_set_vsync_interval(int interval);

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern int vita_port_get_vsync_interval();

	/// <summary>Port: refreshes each flip waits for right now (0: no vsync); 1 off the Vita.</summary>
	public static int GetVsyncInterval()
	{
		return IsVita ? vita_port_get_vsync_interval() : 1;
	}

	/// <summary>Port: how many display refreshes each frame stays up (1: 60 fps, 2: 30 fps), from the next flip on.</summary>
	public static void SetVsyncInterval(int interval)
	{
		if (IsVita)
		{
			vita_port_set_vsync_interval(interval);
		}
	}

	/// <summary>Port: free vitaGL memory (VRAM and RAM pools together) in KB; int.MaxValue off the Vita.</summary>
	public static int GraphicsFreeKB()
	{
		return IsVita ? vita_port_gl_free_kb() : int.MaxValue;
	}

	/// <summary>Port: rear touchpad taps (left / right half), which shift the toolbar like LT / RT, as in the official Vita release.</summary>
	public static int ReadRearTouchTaps()
	{
		return IsVita ? vita_port_rear_touch_taps() : 0;
	}

	private static readonly Stopwatch Clock = Stopwatch.StartNew();

	private const double FrameReportSeconds = 10.0;

	private static double _reportStart;

	private static double _phaseStart;

	private static int _frames;

	private static double _updateTotal;

	/// <summary>Update calls since the last report: under 60 a second the game itself runs slower than real time.</summary>
	private static int _updates;

	private static double _updateMax;

	private static double _drawTotal;

	private static double _drawMax;

	private static int _gcAtReport;

	private static readonly System.Collections.Generic.Dictionary<string, double> _sections = new System.Collections.Generic.Dictionary<string, double>();

	/// <summary>Port development aid: a timestamp for <see cref="EndSection" />.</summary>
	public static double BeginSection()
	{
		return IsVita ? Clock.Elapsed.TotalMilliseconds : 0.0;
	}

	/// <summary>Port development aid: milliseconds since <paramref name="start" /> (from <see cref="BeginSection" />); 0 off the Vita.</summary>
	public static double ElapsedSince(double start)
	{
		return IsVita ? Clock.Elapsed.TotalMilliseconds - start : 0.0;
	}

	/// <summary>Port development aid: a line when the time since <paramref name="start" /> (from <see cref="BeginSection" />) reaches <paramref name="milliseconds" />.</summary>
	public static void TraceIfSlow(string what, double start, double milliseconds)
	{
		if (IsVita)
		{
			double spent = Clock.Elapsed.TotalMilliseconds - start;
			if (spent >= milliseconds)
			{
				Trace($"slow: {what} {spent:F0} ms");
			}
		}
	}

	/// <summary>Port development aid: adds the time since <paramref name="start" /> to a named part of the frame, reported with the fps line.</summary>
	public static void EndSection(string name, double start)
	{
		if (IsVita)
		{
			_sections.TryGetValue(name, out double total);
			_sections[name] = total + (Clock.Elapsed.TotalMilliseconds - start);
		}
	}

	/// <summary>Port development aid: parts of the frame timed many times per frame (per location), kept in fixed slots so timing them stays cheap. Reported with the named sections.</summary>
	public enum HotSection
	{
		/// <summary>The current location's UpdateWhenCurrentLocation.</summary>
		Here,
		/// <summary>The objects loop in <see cref="Here" />.</summary>
		HereObjects,
		/// <summary>The terrain features loop in <see cref="Here" />.</summary>
		HereTerrain,
		/// <summary>Every location's updateEvenIfFarmerIsntHere, the current one's included.</summary>
		AllLocations,
		/// <summary>The NPC updates in <see cref="AllLocations" />.</summary>
		AllLocationsNpcs,
		/// <summary>Characters following a path (PathFindController.update), in <see cref="AllLocationsNpcs" /> for NPCs.</summary>
		NpcPath,
		/// <summary>In <see cref="NpcPath" />: checking the other NPCs of the location for one in the way.</summary>
		NpcCrowd,
		/// <summary>In <see cref="NpcPath" />: the step itself (MovePosition and its collision check).</summary>
		NpcMove,
		/// <summary>In <see cref="NpcPath" />: the warp and door check after the step.</summary>
		NpcWarps
	}

	private static readonly string[] HotSectionNames = new string[9] { "here", "here: objects", "here: terrain", "all locations", "all locations: npcs", "npcs: path", "npcs: path: crowd", "npcs: path: move", "npcs: path: warps" };

	private static readonly long[] _hotTicks = new long[9];

	/// <summary>Port development aid: a timestamp for <see cref="EndHotSection" />.</summary>
	public static long BeginHotSection()
	{
		return ProfileSections ? Stopwatch.GetTimestamp() : 0L;
	}

	/// <summary>Port development aid: adds the time since <paramref name="start" /> to a <see cref="HotSection" />.</summary>
	public static void EndHotSection(HotSection section, long start)
	{
		if (ProfileSections)
		{
			_hotTicks[(int)section] += Stopwatch.GetTimestamp() - start;
		}
	}

	/// <summary>An Update or Draw that takes longer than this gets its own line, with what it spent the time on.</summary>
	private const double SpikeMilliseconds = 50.0;

	private static readonly System.Collections.Generic.Dictionary<string, double> _sectionsAtPhaseStart = new System.Collections.Generic.Dictionary<string, double>();

	private static readonly long[] _hotTicksAtPhaseStart = new long[9];

	private static int _gcAtPhaseStart;

	private static int _majorGcAtPhaseStart;

	/// <summary>Loading screens alone would fill the log with spikes.</summary>
	private const int MaxSpikesTraced = 400;

	private static int _spikesTraced;

	/// <summary>Port development aid: marks the start of GameRunner.Update or Draw (see <see cref="EndFramePhase" />).</summary>
	public static void BeginFramePhase()
	{
		if (IsVita)
		{
			_sectionsAtPhaseStart.Clear();
			foreach (System.Collections.Generic.KeyValuePair<string, double> section in _sections)
			{
				_sectionsAtPhaseStart[section.Key] = section.Value;
			}
			Array.Copy(_hotTicks, _hotTicksAtPhaseStart, _hotTicks.Length);
			_gcAtPhaseStart = GC.CollectionCount(0);
			_majorGcAtPhaseStart = GC.CollectionCount(1);
			_phaseStart = Clock.Elapsed.TotalMilliseconds;
		}
	}

	/// <summary>Port: logs the methods the sampling profiler saw in the last elapsed_ms (vita-log.txt, [spike stacks]); does nothing unless profile-samples is in the game folder (mono-vita/vita-sampler.c).</summary>
	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern void vita_sampler_spike(uint elapsedMs, int draw);

	/// <summary>Port development aid: one line for an Update or Draw over <see cref="SpikeMilliseconds" />: the sections it spent at least 5 ms in, and whether a GC ran in it.</summary>
	private static void TraceSpike(bool draw, double elapsed)
	{
		if (_spikesTraced >= MaxSpikesTraced)
		{
			return;
		}
		_spikesTraced++;
		var parts = new System.Text.StringBuilder();
		parts.Append($"spike: {(draw ? "draw" : "update")} {elapsed:F0} ms;");
		int gcs = GC.CollectionCount(0) - _gcAtPhaseStart;
		if (gcs > 0)
		{
			parts.Append($" {gcs} GC{(GC.CollectionCount(1) != _majorGcAtPhaseStart ? " (major)" : "")};");
		}
		foreach (System.Collections.Generic.KeyValuePair<string, double> section in _sections)
		{
			_sectionsAtPhaseStart.TryGetValue(section.Key, out double before);
			double spent = section.Value - before;
			if (spent >= 5.0)
			{
				parts.Append($" {section.Key} {spent:F0} ms;");
			}
		}
		for (int i = 0; i < _hotTicks.Length; i++)
		{
			double spent = (_hotTicks[i] - _hotTicksAtPhaseStart[i]) * 1000.0 / Stopwatch.Frequency;
			if (spent >= 5.0)
			{
				parts.Append($" {HotSectionNames[i]} {spent:F0} ms;");
			}
		}
		Trace(parts.ToString());
		if (IsVita)
		{
			// After the spike line, so the stacks follow it; the clamp keeps a long load from looking back over the whole ring.
			vita_sampler_spike((uint)Math.Min(elapsed, 2000.0), draw ? 1 : 0);
		}
	}

	/// <summary>The type of the last menu counted in <see cref="_menuFrames" /> (ItemGrabMenu is a chest), for the frames line.</summary>
	private static Type _lastMenuType;

	/// <summary>Frames drawn with a menu open since the last frames line.</summary>
	private static int _menuFrames;

	/// <summary>The camera level label last written to the log ("-" outside play): a "camera:" line marks each change.</summary>
	private static string _lastCameraTraced;

	/// <summary>Port: frames in this report that drew the back layers, floors and soil from Game1's cached pictures, and how many times those pictures were redrawn (the frames line): whether they are doing their job at a camera level.</summary>
	public static readonly int[] CachedFrames = new int[4];

	/// <summary>Port development aid: every 10 s, a line in console.txt with fps and the CPU time of Update and Draw.</summary>
	/// <remarks>Draw only records SpriteBatch work on the CPU; the GPU wait shows up in vita-log.txt (swap time).</remarks>
	public static void EndFramePhase(bool draw)
	{
		if (!IsVita)
		{
			return;
		}
		double now = Clock.Elapsed.TotalMilliseconds;
		double elapsed = now - _phaseStart;
		if (elapsed >= SpikeMilliseconds && _frames > 0)
		{
			TraceSpike(draw, elapsed);
		}
		if (draw)
		{
			_drawTotal += elapsed;
			_drawMax = Math.Max(_drawMax, elapsed);
			_frames++;
			// Which camera level the frame was drawn at, and whether a menu was open over the world (a
			// menu adds its own draw and, in the options menu, the camera changes): fps per camera level.
			if (Game1.activeClickableMenu != null)
			{
				_menuFrames++;
				_lastMenuType = Game1.activeClickableMenu.GetType();
			}
			string camera = (Game1.gameMode == Game1.playingGameMode) ? CameraZoomToken : "-";
			if (!ReferenceEquals(camera, _lastCameraTraced))
			{
				_lastCameraTraced = camera;
				Trace($"camera: {camera}");
			}
		}
		else
		{
			_updateTotal += elapsed;
			_updates++;
			_updateMax = Math.Max(_updateMax, elapsed);
		}
		if (draw && now - _reportStart >= FrameReportSeconds * 1000.0)
		{
			int gcs = GC.CollectionCount(0);
			Trace($"frames: {_frames / ((now - _reportStart) / 1000.0):F1} fps, {_updates / ((now - _reportStart) / 1000.0):F1} updates/s; update avg {_updateTotal / Math.Max(_frames, 1):F1} ms max {_updateMax:F1}; draw avg {_drawTotal / Math.Max(_frames, 1):F1} ms max {_drawMax:F1}; GCs {gcs - _gcAtReport}; cap {((FpsLimit == 0) ? "auto" : FpsLimit.ToString())}, vsync {GetVsyncInterval()}; camera {_lastCameraTraced}, menu open {100 * _menuFrames / Math.Max(_frames, 1)}% of frames{((_menuFrames > 0) ? (" (" + _lastMenuType?.Name + ")") : "")}; cached pictures used: back {100 * CachedFrames[0] / Math.Max(_frames, 1)}%, floors {100 * CachedFrames[1] / Math.Max(_frames, 1)}%, soil {100 * CachedFrames[2] / Math.Max(_frames, 1)}%, redrawn {CachedFrames[3]} times");
			GameLocation scene = Game1.currentLocation;
			if (scene != null)
			{
				Trace($"scene: {scene.NameOrUniqueName}; outdoors {(scene.IsOutdoors ? 1 : 0)}, rain {(scene.IsRainingHere() ? 1 : 0)}, animals {scene.animals.Length}");
			}
			_menuFrames = 0;
			System.Array.Clear(CachedFrames, 0, CachedFrames.Length);
			for (int i = 0; i < _hotTicks.Length; i++)
			{
				if (_hotTicks[i] != 0)
				{
					_sections[HotSectionNames[i]] = _hotTicks[i] * 1000.0 / Stopwatch.Frequency;
					_hotTicks[i] = 0;
				}
			}
			if (_sections.Count > 0)
			{
				var parts = new System.Text.StringBuilder("  per frame:");
				foreach (System.Collections.Generic.KeyValuePair<string, double> section in System.Linq.Enumerable.OrderByDescending(_sections, s => s.Value))
				{
					parts.Append($" {section.Key} {section.Value / Math.Max(_frames, 1):F1} ms;");
				}
				Trace(parts.ToString());
				_sections.Clear();
			}
			_reportStart = now;
			_frames = 0;
			_updates = 0;
			_updateTotal = _updateMax = _drawTotal = _drawMax = 0.0;
			_gcAtReport = gcs;
		}
	}

	[DllImport("VitaPort", CallingConvention = CallingConvention.Cdecl)]
	private static extern void vita_port_console_write([MarshalAs(UnmanagedType.LPStr)] string text);

	/// <summary>Port: console output for ErrorLogs/console.txt, handed whole lines to the launcher, whose own thread writes them to the card about once a second (Tools/Vita/stardew/main.c). Writing them with AutoFlush held the main thread for each card write (50-130 ms spikes every 10 s, at the fps line).</summary>
	public sealed class ConsoleWriter : System.IO.TextWriter
	{
		private readonly System.Text.StringBuilder _line = new System.Text.StringBuilder();

		public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

		public override void Write(char value)
		{
			try
			{
				_line.Append(value);
				if (value == '\n')
				{
					Flush();
				}
			}
			catch (OutOfMemoryException)
			{
				_line.Clear();
			}
		}

		public override void Write(string value)
		{
			if (value == null)
			{
				return;
			}
			try
			{
				if (_line.Length == 0 && value.EndsWith("\n"))
				{
					vita_port_console_write(value);
					return;
				}
				_line.Append(value);
				if (value.IndexOf('\n') >= 0)
				{
					Flush();
				}
			}
			catch (OutOfMemoryException)
			{
				_line.Clear();
				try
				{
					vita_port_console_write(value);
				}
				catch
				{
				}
			}
		}

		public override void Flush()
		{
			try
			{
				if (_line.Length > 0)
				{
					vita_port_console_write(_line.ToString());
					_line.Clear();
				}
			}
			catch (OutOfMemoryException)
			{
				_line.Clear();
			}
		}
	}

	/// <summary>Port development aid: a timestamped line in ErrorLogs/console.txt on the Vita (startup timing).</summary>
	public static void Trace(string message)
	{
		if (IsVita)
		{
			Console.WriteLine($"[{Clock.Elapsed.TotalSeconds,7:F2}s] {message}");
		}
	}

	private static string _cachedHostIp;
	private static DateTime _lastIpCheck = DateTime.MinValue;

	/// <summary>Finds the device's local network IP address (e.g. 192.168.x.x) for LAN co-op hosting.</summary>
	public static string GetHostIpAddress(bool forceRefresh = false)
	{
		if (!forceRefresh && _cachedHostIp != null && (DateTime.UtcNow - _lastIpCheck).TotalSeconds < 10)
		{
			return _cachedHostIp;
		}

		_lastIpCheck = DateTime.UtcNow;

		// 1. Try UDP routing query to common destinations (Connect on UDP does not send packets over the network, only queries routing table)
		string[] probeTargets = new string[] { "8.8.8.8", "1.1.1.1", "192.168.1.1", "10.0.0.1" };
		foreach (string target in probeTargets)
		{
			try
			{
				using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
				{
					socket.Connect(target, 65530);
					IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
					if (endPoint != null && endPoint.Address != null)
					{
						string ip = endPoint.Address.ToString();
						if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && !IPAddress.IsLoopback(endPoint.Address))
						{
							_cachedHostIp = ip;
							return ip;
						}
					}
				}
			}
			catch
			{
			}
		}

		// 2. Fallback: Dns query
		try
		{
			IPAddress[] hostAddresses = Dns.GetHostAddresses(Dns.GetHostName());
			if (hostAddresses != null)
			{
				foreach (IPAddress addr in hostAddresses)
				{
					if (addr != null && addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
					{
						string ip = addr.ToString();
						if (!string.IsNullOrEmpty(ip) && ip != "0.0.0.0")
						{
							_cachedHostIp = ip;
							return ip;
						}
					}
				}
			}
		}
		catch
		{
		}

		return _cachedHostIp;
	}

	private static bool Detect()
	{
		try
		{
			return SDL.SDL_GetPlatform() == "PlayStation Vita";
		}
		catch (Exception)
		{
			return false;
		}
	}
}
