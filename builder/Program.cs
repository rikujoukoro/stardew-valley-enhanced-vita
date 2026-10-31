// Builds the Vita port's game assemblies from the player's own Stardew Valley 1.6.15 (compatibility branch):
// finds and verifies the game, decompiles it the way the patches expect, applies our patches and files,
// fetches FNA, and builds with the .NET SDK. Nothing of the game is in this repository.
//
//   dotnet run --project builder -- [--game <Stardew Valley folder>] [--work <folder>]
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Metadata;

static partial class Program
{
	static readonly string[] Deps = { "xTile", "BmFont", "StardewValley.GameData", "Lidgren.Network" };

	static string Repo;

	static int Main(string[] args)
	{
		try
		{
			Repo = FindRepo();
			if (args.Length == 4 && args[0] == "--dump-texture")
			{
				// Maintenance: --dump-texture <file.xnb> <out.png> <FNA.dll>
				Rgba.FromXnb(File.ReadAllBytes(args[1]), args[3]).WritePng(args[2]);
				return 0;
			}
			if (args.Length == 5 && args[0] == "--render-art")
			{
				// Maintenance: --render-art <Content dir> <FNA.dll> <PEGI 12 png> <out dir>
				LiveArea.Write(args[1], args[2], Repo, args[3], args[4]);
				return 0;
			}
			// Trim('"'): in Windows' shells "C:\folder\" passes the closing quote along.
			string game = Arg(args, "--game")?.Trim('"') ?? FindGame();
			string work = Path.GetFullPath(Arg(args, "--work")?.Trim('"') ?? Path.Combine(Repo, "work"));
			CheckPathLength(work);
			bool vpk = !args.Contains("--no-vpk");
			string toolchain = Arg(args, "--toolchain")?.Trim('"') ?? (vpk ? LocalToolchain() : null);
			Step("Game: " + game);
			GameBuild = Verify(game);
			if (vpk)
			{
				CheckToolchain(toolchain);
			}
			string src = Path.Combine(work, "src");
			string tree = Path.Combine(src, "SV_PC_source");
			if (Directory.Exists(src))
			{
				Directory.Delete(src, true);
			}
			string bin = GameInput(game, Path.Combine(work, "game"));
			Step("Fetching and building FNA");
			string fna = FetchFna(Path.Combine(src, "FNA"), Path.Combine(work, "downloads"));
			FrameworkRefs = FetchFrameworkReferences(Path.Combine(work, "downloads"), Path.Combine(work, "cache"));
			Step("Decompiling Stardew Valley.exe");
			Decompile(Path.Combine(bin, "Stardew Valley.exe"), tree, usePdb: true, fna);
			File.Delete(Path.Combine(tree, "Stardew Valley.csproj"));
			foreach (string dep in Deps)
			{
				Step("Decompiling " + dep + ".dll");
				string dir = Path.Combine(tree, "Deps", dep);
				Decompile(Path.Combine(bin, dep + ".dll"), dir, usePdb: false, fna);
				File.Delete(Path.Combine(dir, dep + ".csproj"));
				if (dep == "Lidgren.Network")
				{
					// ilspycmd nests the root namespace's files; our patches expect them flat.
					string nested = Path.Combine(dir, dep);
					foreach (string f in Directory.GetFiles(nested))
					{
						File.Move(f, Path.Combine(dir, Path.GetFileName(f)));
					}
					Directory.Delete(nested);
				}
			}
			if (args.Contains("--decompile-only"))
			{
				return 0;
			}
			Step("Applying patches");
			if (GameBuild.Prepatches != null)
			{
				// Another store's build of the same game: first into the source the Steam build decompiles to.
				ApplyAll(Path.Combine(Repo, "patches", GameBuild.Prepatches), tree);
			}
			foreach (string rel in File.ReadAllLines(Path.Combine(Repo, "patches", "game.deleted.txt")).Where(l => l.Trim() != ""))
			{
				// Exists: the GOG build lacks the Steam-only files.
				if (File.Exists(Path.Combine(tree, rel.Trim())))
				{
					File.Delete(Path.Combine(tree, rel.Trim()));
				}
			}
			ApplyAll(Path.Combine(Repo, "patches", "game"), tree);
			foreach (string dep in Deps)
			{
				ApplyAll(Path.Combine(Repo, "patches", "deps", dep), Path.Combine(tree, "Deps", dep));
			}
			CopyTree(Path.Combine(Repo, "overlay", "game"), tree);
			CopyTree(Path.Combine(Repo, "overlay", "deps"), Path.Combine(tree, "Deps"));
			string outDir = Path.Combine(work, "out");
			// The layout before out/ux0 (older builder versions).
			if (Directory.Exists(Path.Combine(outDir, "ux0_data_StardewValley")))
			{
				Directory.Delete(Path.Combine(outDir, "ux0_data_StardewValley"), true);
			}
			if (File.Exists(Path.Combine(outDir, "stardew.vpk")))
			{
				File.Delete(Path.Combine(outDir, "stardew.vpk"));
			}
			Build(tree, bin, outDir, args.Contains("--save-serializers"));
			Stage(tree, game, fna, GameData(outDir), Path.Combine(outDir, "sce_sys"), Path.Combine(work, "cache"));
			List<string> farms = args.Contains("--no-saves") ? new List<string>() : CopySaves(Arg(args, "--saves")?.Trim('"'), GameData(outDir));
			if (vpk)
			{
				Step("AOT-compiling for the Vita");
				MakeVpk(work, outDir, fna, toolchain, Arg(args, "--native-dir")?.Trim('"'));
				VitaPlugins(outDir, Path.Combine(work, "downloads"));
				// Inputs of vpk/build-vpk.sh (the --no-vpk path), now in the VPK: out keeps only what the player copies.
				File.Delete(Path.Combine(outDir, "FNA.dll"));
				Directory.Delete(Path.Combine(outDir, "sce_sys"), true);
			}
			Step("Done");
			string ux0 = Path.Combine(outDir, "ux0");
			Console.WriteLine();
			Console.WriteLine("Copy everything in this folder to the Vita's ux0: (VitaShell: SELECT for USB or FTP), then install");
			Console.WriteLine("ux0:stardew.vpk with VitaShell.");
			Console.WriteLine("   " + (vpk ? ux0 : GameData(outDir)));
			if (vpk)
			{
				Console.WriteLine();
				Console.WriteLine("First time on this Vita? " + Path.Combine(outDir, PluginNote) + " says how to set up kubridge,");
				Console.WriteLine("libshacccg (with ShaRKF00D) and NoTrpDrm; their files are next to it.");
			}
			if (farms.Count > 0)
			{
				Console.WriteLine();
				Console.WriteLine("Your PC saves are in it too (" + string.Join(", ", farms) + "). A farm that is also on the Vita gets");
				Console.WriteLine("replaced by the PC's copy: if you played it further on the Vita, don't let Windows replace that folder.");
			}
			return 0;
		}
		catch (BuildException e)
		{
			Console.Error.WriteLine();
			Console.Error.WriteLine("ERROR: " + e.Message);
			return 1;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
		{
			// Disk, permission and network trouble: the message says what, the stack trace wouldn't help.
			Console.Error.WriteLine();
			Console.Error.WriteLine("ERROR: " + e.GetType().Name + ": " + e.Message);
			return 1;
		}
	}

	// The output mirrors the memory card: out/ux0/data/StardewValley and out/ux0/stardew.vpk, so the player copies
	// out/ux0's contents to ux0: as they are.
	static string GameData(string outDir)
	{
		return Path.Combine(outDir, "ux0", "data", "StardewValley");
	}

	// The player's PC saves (one folder per farm) into the output's Saves, so one copy brings them to the Vita. The
	// game keeps them in ApplicationData/StardewValley/Saves: %APPDATA% on Windows, ~/.config on Linux and macOS.
	static List<string> CopySaves(string from, string gameData)
	{
		from ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewValley", "Saves");
		var farms = new List<string>();
		if (!Directory.Exists(from))
		{
			return farms;
		}
		foreach (string farm in Directory.GetDirectories(from).OrderBy(d => d, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(farm);
			// A farm folder holds a save named like the folder and SaveGameInfo; anything else isn't a save.
			if (!File.Exists(Path.Combine(farm, name)) || !File.Exists(Path.Combine(farm, "SaveGameInfo")))
			{
				continue;
			}
			CopyTree(farm, Path.Combine(gameData, "Saves", name));
			farms.Add(name);
		}
		if (farms.Count > 0)
		{
			Step("Copied your PC saves: " + string.Join(", ", farms));
		}
		return farms;
	}

	const string PluginNote = "VITA-PLUGINS.txt";

	// What the Vita needs besides the game (tools/sources.json "vitaplugins"), from each author's release, laid out
	// like the memory cards as out/ux0 is (kubridge in out/ur0/tai), with a note on setting them up. libshacccg.suprx
	// is Sony's and can't be shared: ShaRKF00D.vpk extracts it on the Vita.
	static void VitaPlugins(string outDir, string cache)
	{
		Step("Fetching the Vita plugins (kubridge, NoTrpDrm, ShaRKF00D)");
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
		foreach (JsonElement file in doc.RootElement.GetProperty("vitaplugins").GetProperty("files").EnumerateArray())
		{
			string url = file.GetProperty("url").GetString();
			byte[] data = Download(url, cache);
			if (Sha256(data) != file.GetProperty("sha256").GetString())
			{
				throw new BuildException("Unexpected content from " + url);
			}
			string target = Path.Combine(outDir, file.GetProperty("path").GetString().Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			File.WriteAllBytes(target, data);
		}
		string[] note =
		{
			"Stardew Valley Enhanced: setting up a Vita for the first time",
			"=============================================================",
			"",
			"The game needs kubridge and libshacccg; NoTrpDrm is optional (real trophies). Skip what your Vita",
			"already has. The files are in this folder, laid out like the Vita's memory: ur0\\... goes to ur0:, ux0\\...",
			"to ux0: (copying the ux0 folder for the game brings ux0\\tai\\NoTrpDrm.suprx and ux0\\ShaRKF00D.vpk along).",
			"",
			"Which config.txt: if ux0:tai/config.txt exists, the Vita reads that one and ignores ur0:tai/config.txt.",
			"Edit the one your Vita uses (VitaShell: select the file, press Triangle, \"Open in text editor\").",
			"",
			"1. kubridge (needed; v0.3.1_hotfix: older ones crash the game at the start)",
			"   - Copy ur0\\tai\\kubridge.skprx to ur0:tai/ (replace an older kubridge.skprx). VitaShell's USB mode",
			"     only shows ux0:, so copy it there first and move it to ur0:tai/ in VitaShell, or use FTP.",
			"   - In config.txt, under *KERNEL, one line (and no other kubridge line):",
			"         ur0:tai/kubridge.skprx",
			"",
			"2. libshacccg.suprx (needed for the graphics; Sony's file, made on your own Vita)",
			"   - Already there if ur0:data/libshacccg.suprx or ur0:data/external/libshacccg.suprx exists.",
			"   - Otherwise: install PSM Runtime with its 2.01 update (from the PS Store), install ux0:ShaRKF00D.vpk",
			"     with VitaShell and start it. It closes by itself when done and leaves ur0:data/libshacccg.suprx.",
			"",
			"3. NoTrpDrm (optional: the game's trophies as real trophies)",
			"   - ux0\\tai\\NoTrpDrm.suprx goes to ux0:tai/NoTrpDrm.suprx.",
			"   - In config.txt, under *main (add the *main line if there is none):",
			"         ux0:tai/NoTrpDrm.suprx",
			"",
			"Restart the Vita after changing config.txt, then start the game.",
			"",
			"Where these come from (each author's own release, checked by SHA-256):",
			"   kubridge   https://github.com/bythos14/kubridge (bythos14)",
			"   NoTrpDrm   https://github.com/Rinnegatamante/NoTrpDrm (Rinnegatamante, LGPL-3.0)",
			"   ShaRKF00D  https://github.com/OsirizX/ShaRKF00D (OsirizX)",
		};
		// CRLF: read in Windows' Notepad.
		File.WriteAllText(Path.Combine(outDir, PluginNote), string.Join("\r\n", note) + "\r\n");
	}

	// The toolchain zip when it sits next to the builder (this folder or the one above), as in the release bundle.
	static string LocalToolchain()
	{
		string name = "vita-toolchain-" + Host() + ".zip";
		foreach (string dir in new[] { Repo, Path.GetDirectoryName(Repo) })
		{
			if (dir != null && File.Exists(Path.Combine(dir, name)))
			{
				return Path.Combine(dir, name);
			}
		}
		return null;
	}

	sealed class BuildException : Exception
	{
		public BuildException(string message)
			: base(message)
		{
		}
	}

	static string Arg(string[] args, string name)
	{
		int i = Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
	}

	static void Step(string text)
	{
		Console.WriteLine("== " + text);
	}

	// The decompiled game's deepest files sit ~130 characters under the work folder; MSBuild and the compilers
	// can't reach them past Windows' 260-character limit unless long paths are turned on.
	static void CheckPathLength(string work)
	{
		if (!OperatingSystem.IsWindows() || work.Length + 140 < 260)
		{
			return;
		}
		using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
		if (key?.GetValue("LongPathsEnabled") is int enabled && enabled == 1)
		{
			return;
		}
		throw new BuildException("The work folder's path is too long for Windows (" + work.Length + " characters): " + work
			+ "\nMove this folder somewhere shorter (like C:\\Games\\vita-builder) or pass --work C:\\svb-work.");
	}

	static string FindRepo()
	{
		for (string dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
		{
			if (File.Exists(Path.Combine(dir, "tools", "known-games.json")))
			{
				return dir;
			}
		}
		throw new BuildException("tools/known-games.json not found above " + AppContext.BaseDirectory);
	}

	// --- Finding and verifying the game ---

	// The game folders to try, in this order: a "Stardew Valley" folder put into the builder's folder (or the one
	// above, as for the toolchain zip), Steam's libraries, then GOG Galaxy's install. The first one that is a known
	// build wins; if none is, the first one found, so Verify says what is wrong with it.
	static string FindGame()
	{
		var found = new List<string>();
		foreach (string dir in new[] { Repo, Path.GetDirectoryName(Repo) })
		{
			if (dir != null)
			{
				found.Add(Path.Combine(dir, "Stardew Valley"));
			}
		}
		found.AddRange(SteamGames());
		found.AddRange(GogGames());
		found = found.Where(d => File.Exists(Path.Combine(d, "Stardew Valley.exe"))).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (found.Count == 0)
		{
			throw new BuildException("Stardew Valley not found in your Steam libraries or GOG Galaxy. Copy your game folder (the one with"
				+ " Stardew Valley.exe) into this folder as \"Stardew Valley\", or pass --game \"<folder with Stardew Valley.exe>\".");
		}
		return found.FirstOrDefault(d => MatchBuild(d, new List<string>()) != null) ?? found[0];
	}

	// GOG Galaxy writes each game's folder to the registry (1453375253 is Stardew Valley's GOG id).
	static IEnumerable<string> GogGames()
	{
		if (!OperatingSystem.IsWindows())
		{
			yield break;
		}
		foreach (string keyPath in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\1453375253", @"SOFTWARE\GOG.com\Games\1453375253" })
		{
			using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(keyPath);
			if (key?.GetValue("path") is string path)
			{
				yield return path;
			}
		}
		yield return @"C:\Program Files (x86)\GOG Galaxy\Games\Stardew Valley";
		yield return @"C:\GOG Games\Stardew Valley";
	}

	static IEnumerable<string> SteamGames()
	{
		var libraries = new List<string>();
		if (OperatingSystem.IsWindows())
		{
			using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
			if (key?.GetValue("SteamPath") is string steam)
			{
				libraries.Add(steam);
			}
			libraries.Add(@"C:\Program Files (x86)\Steam");
		}
		else
		{
			string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			libraries.Add(Path.Combine(home, ".steam", "steam"));
			libraries.Add(Path.Combine(home, ".local", "share", "Steam"));
			// Flatpak and Snap Steam.
			libraries.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
			libraries.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
		}
		foreach (string steam in libraries.ToList())
		{
			string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
			if (File.Exists(vdf))
			{
				foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
				{
					libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
				}
			}
		}
		return libraries.Distinct(StringComparer.OrdinalIgnoreCase).Select(lib => Path.Combine(lib, "steamapps", "common", "Stardew Valley")).ToList();
	}

	// The verified build: its patches/<Prepatches> (null for Steam) and its XML serializer pack (keyed to its exe).
	sealed record KnownBuild(string Name, string Prepatches, string SerializerPack);

	static KnownBuild GameBuild;

	// The known build whose files the folder has; null with the closest build's mismatches otherwise.
	static KnownBuild MatchBuild(string game, List<string> closest)
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "known-games.json")));
		closest.Clear();
		foreach (JsonElement build in doc.RootElement.GetProperty("builds").EnumerateArray())
		{
			var mismatches = new List<string>();
			foreach (JsonProperty file in build.GetProperty("files").EnumerateObject())
			{
				string path = Path.Combine(game, file.Name);
				if (!File.Exists(path) || Sha256(File.ReadAllBytes(path)) != file.Value.GetString())
				{
					mismatches.Add(file.Name);
				}
			}
			if (mismatches.Count == 0)
			{
				string prepatches = build.TryGetProperty("prepatches", out JsonElement p) ? p.GetString() : null;
				string pack = build.TryGetProperty("serializers", out JsonElement s) ? s.GetString() : "xmlserializers.bin";
				return new KnownBuild(build.GetProperty("name").GetString(), prepatches, pack);
			}
			if (closest.Count == 0 || mismatches.Count < closest.Count)
			{
				closest.Clear();
				closest.AddRange(mismatches);
			}
		}
		return null;
	}

	static KnownBuild Verify(string game)
	{
		if (!Directory.Exists(game))
		{
			throw new BuildException("No such folder: " + game);
		}
		var mismatches = new List<string>();
		KnownBuild build = MatchBuild(game, mismatches);
		if (build != null)
		{
			Step("Verified: " + build.Name);
			return build;
		}
		throw new BuildException("Your game files don't match Stardew Valley 1.6.15 (compatibility branch): " + string.Join(", ", mismatches)
			+ ".\nSteam: Library > Stardew Valley > Properties > Betas > select \"compatibility\", let it update, then run this again."
			+ "\nGOG: install the game's compatibility version (1.6.15) in GOG Galaxy."
			+ (OperatingSystem.IsWindows() ? "" : "\nThe builder needs the game's Windows files: on Linux also set Properties > Compatibility > \"Force the use of a"
				+ " specific Steam Play compatibility tool\" (Proton); on macOS download them with DepotDownloader (see the README)."));
	}

	// The game's assemblies the build reads, copied into work/game: next to them in the game folder, other DLLs
	// (a GOG folder can keep .NET 6 System.dll and mscorlib.dll from the store's main version) would change what the
	// decompiler resolves and so the source the patches meet. Content stays where it is.
	static readonly string[] GameReferences = { "GalaxyCSharp.dll", "Steamworks.NET.dll", "System.Data.HashFunction.Core.dll",
		"System.Data.HashFunction.Interfaces.dll", "System.Data.HashFunction.xxHash.dll" };

	static string GameInput(string game, string dir)
	{
		if (Directory.Exists(dir))
		{
			Directory.Delete(dir, true);
		}
		Directory.CreateDirectory(dir);
		foreach (string name in new[] { "Stardew Valley.exe" }.Concat(Deps.Select(d => d + ".dll")).Concat(GameReferences))
		{
			if (File.Exists(Path.Combine(game, name)))
			{
				File.Copy(Path.Combine(game, name), Path.Combine(dir, name));
			}
		}
		return dir;
	}

	static string Sha256(byte[] data)
	{
		return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
	}

	// --- Decompiling (same output as `ilspycmd -p [-usepdb] --ilspy-settingsfile tools/ilspy-settings.xml`) ---

	static DecompilerSettings LoadSettings()
	{
		var settings = new DecompilerSettings();
		XElement section = XDocument.Load(Path.Combine(Repo, "tools", "ilspy-settings.xml")).Root.Element("DecompilerSettings");
		foreach (XAttribute attr in section.Attributes())
		{
			var prop = typeof(DecompilerSettings).GetProperty(attr.Name.LocalName);
			if (prop != null && prop.PropertyType == typeof(bool) && prop.CanWrite)
			{
				prop.SetValue(settings, bool.Parse(attr.Value));
			}
		}
		return settings;
	}

	// .NET Framework 4.8's reference assemblies (NuGet), where the decompiler finds the game's framework references:
	// otherwise Windows takes them from its installed .NET Framework and Linux and macOS have none.
	static string FrameworkRefs;

	static string FetchFrameworkReferences(string downloads, string cache)
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
		JsonElement refs = doc.RootElement.GetProperty("netfx48refs");
		string sha = refs.GetProperty("sha256").GetString();
		string dir = Path.Combine(cache, "netfx48-" + sha.Substring(0, 16));
		if (File.Exists(Path.Combine(dir, "mscorlib.dll")))
		{
			return dir;
		}
		byte[] nupkg = Download(refs.GetProperty("url").GetString(), downloads);
		if (Sha256(nupkg) != sha)
		{
			throw new BuildException("Unexpected content from " + refs.GetProperty("url").GetString());
		}
		string root = refs.GetProperty("root").GetString() + "/";
		Directory.CreateDirectory(dir + ".part");
		using (var zip = new ZipArchive(new MemoryStream(nupkg)))
		{
			foreach (ZipArchiveEntry e in zip.Entries)
			{
				// The assemblies and their facades, one folder: the decompiler looks a reference up by name.
				if (e.FullName.StartsWith(root, StringComparison.Ordinal) && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				{
					e.ExtractToFile(Path.Combine(dir + ".part", e.Name), true);
				}
			}
		}
		if (Directory.Exists(dir))
		{
			Directory.Delete(dir, true);
		}
		Directory.Move(dir + ".part", dir);
		return dir;
	}

	// Serves the game's XNA references from our FNA.dll (same API), so the output doesn't depend on whether
	// the XNA redistributable is installed (without any XNA, ~500 files come out different), and its framework
	// references from FrameworkRefs. The game's own assemblies come from its folder.
	sealed class XnaResolver : IAssemblyResolver
	{
		readonly UniversalAssemblyResolver inner;

		readonly MetadataFile fna;

		readonly string gameDir;

		readonly Dictionary<string, MetadataFile> framework = new Dictionary<string, MetadataFile>(StringComparer.OrdinalIgnoreCase);

		public XnaResolver(UniversalAssemblyResolver inner, string fnaPath, string gameDir)
		{
			this.inner = inner;
			fna = new PEFile(fnaPath);
			this.gameDir = gameDir;
		}

		public MetadataFile Resolve(IAssemblyReference reference)
		{
			if (reference.Name.StartsWith("Microsoft.Xna", StringComparison.Ordinal))
			{
				return fna;
			}
			string refPath = Path.Combine(FrameworkRefs, reference.Name + ".dll");
			if (File.Exists(Path.Combine(gameDir, reference.Name + ".dll")) || File.Exists(Path.Combine(gameDir, reference.Name + ".exe"))
				|| !File.Exists(refPath))
			{
				return inner.Resolve(reference);
			}
			lock (framework)
			{
				if (!framework.TryGetValue(reference.Name, out MetadataFile file))
				{
					file = new PEFile(refPath);
					framework[reference.Name] = file;
				}
				return file;
			}
		}

		public MetadataFile ResolveModule(MetadataFile mainModule, string moduleName)
		{
			return inner.ResolveModule(mainModule, moduleName);
		}

		public System.Threading.Tasks.Task<MetadataFile> ResolveAsync(IAssemblyReference reference)
		{
			return System.Threading.Tasks.Task.FromResult(Resolve(reference));
		}

		public System.Threading.Tasks.Task<MetadataFile> ResolveModuleAsync(MetadataFile mainModule, string moduleName)
		{
			return inner.ResolveModuleAsync(mainModule, moduleName);
		}

		public IDisposable BeginSnapshot()
		{
			return inner.BeginSnapshot();
		}
	}

	static void Decompile(string assembly, string outDir, bool usePdb, string fna)
	{
		var module = new PEFile(assembly);
		var resolver = new UniversalAssemblyResolver(assembly, false, module.Metadata.DetectTargetFrameworkId());
		IDebugInfoProvider pdb = null;
		if (usePdb)
		{
			pdb = EmbeddedPdb.Load(module) ?? throw new BuildException("No embedded debug symbols in " + assembly);
		}
		var decompiler = new WholeProjectDecompiler(LoadSettings(), new XnaResolver(resolver, fna, Path.GetDirectoryName(Path.GetFullPath(assembly))), null, resolver, pdb);
		Directory.CreateDirectory(outDir);
		using (var writer = new StreamWriter(File.Create(Path.Combine(outDir, Path.GetFileNameWithoutExtension(assembly) + ".csproj"))))
		{
			decompiler.DecompileProject(module, outDir, writer);
		}
	}

	// Reads the portable PDB embedded in the game (from ILSpyX's PortableDebugInfoProvider, MIT, ILSpy contributors).
	sealed class EmbeddedPdb : IDebugInfoProvider
	{
		readonly MetadataReader reader;

		readonly string fileName;

		EmbeddedPdb(MetadataReader reader, string fileName)
		{
			this.reader = reader;
			this.fileName = fileName;
		}

		public static EmbeddedPdb Load(PEFile module)
		{
			foreach (DebugDirectoryEntry entry in module.Reader.ReadDebugDirectory())
			{
				if (entry.IsPortableCodeView
					&& module.Reader.TryOpenAssociatedPortablePdb(module.FileName, _ => null, out MetadataReaderProvider provider, out _))
				{
					return new EmbeddedPdb(provider.GetMetadataReader(), module.FileName);
				}
			}
			return null;
		}

		public string Description => "Embedded in this assembly";

		public string SourceFileName => fileName;

		public IList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> GetSequencePoints(MethodDefinitionHandle method)
		{
			var list = new List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>();
			try
			{
				foreach (System.Reflection.Metadata.SequencePoint sp in reader.GetMethodDebugInformation(method).GetSequencePoints())
				{
					list.Add(new ICSharpCode.Decompiler.DebugInfo.SequencePoint
					{
						Offset = sp.Offset,
						StartLine = sp.StartLine,
						StartColumn = sp.StartColumn,
						EndLine = sp.EndLine,
						EndColumn = sp.EndColumn,
						DocumentUrl = sp.Document.IsNil ? "" : reader.GetString(reader.GetDocument(sp.Document).Name)
					});
				}
				return list;
			}
			catch (BadImageFormatException)
			{
				return new List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>();
			}
		}

		public IList<Variable> GetVariables(MethodDefinitionHandle method)
		{
			var list = new List<Variable>();
			foreach (LocalScopeHandle scope in reader.GetLocalScopes(method))
			{
				foreach (LocalVariableHandle h in reader.GetLocalScope(scope).GetLocalVariables())
				{
					LocalVariable v = reader.GetLocalVariable(h);
					list.Add(new Variable(v.Index, reader.GetString(v.Name)));
				}
			}
			return list;
		}

		public bool TryGetName(MethodDefinitionHandle method, int index, out string name)
		{
			name = null;
			LocalVariableHandle h = Find(method, index);
			if (h.IsNil)
			{
				return false;
			}
			name = reader.GetString(reader.GetLocalVariable(h).Name);
			return true;
		}

		LocalVariableHandle Find(MethodDefinitionHandle method, int index)
		{
			foreach (LocalScopeHandle scope in reader.GetLocalScopes(method))
			{
				foreach (LocalVariableHandle h in reader.GetLocalScope(scope).GetLocalVariables())
				{
					if (reader.GetLocalVariable(h).Index == index)
					{
						return h;
					}
				}
			}
			return default;
		}

		static readonly Guid TupleElementNames = new Guid("ED9FDF71-8879-4747-8ED3-FE5EDE3CE710");

		static readonly Guid DynamicLocalVariables = new Guid("83C563C4-B4F3-47D5-B824-BA5441477EA8");

		public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo)
		{
			extraTypeInfo = default;
			LocalVariableHandle local = Find(method, index);
			foreach (CustomDebugInformationHandle h in reader.CustomDebugInformation)
			{
				CustomDebugInformation info = reader.GetCustomDebugInformation(h);
				if (info.Parent.IsNil || info.Parent.Kind != HandleKind.LocalVariable || local != (LocalVariableHandle)info.Parent || info.Value.IsNil || info.Kind.IsNil)
				{
					continue;
				}
				Guid kind = reader.GetGuid(info.Kind);
				if (kind == TupleElementNames && extraTypeInfo.TupleElementNames == null)
				{
					BlobReader blob = reader.GetBlobReader(info.Value);
					var names = new List<string>();
					while (blob.RemainingBytes > 0)
					{
						string text = blob.ReadUTF8(blob.IndexOf(0));
						blob.ReadByte();
						names.Add(string.IsNullOrWhiteSpace(text) ? null : text);
					}
					extraTypeInfo.TupleElementNames = names.ToArray();
				}
				else if (kind == DynamicLocalVariables && extraTypeInfo.DynamicFlags == null)
				{
					BlobReader blob = reader.GetBlobReader(info.Value);
					extraTypeInfo.DynamicFlags = new bool[blob.Length * 8];
					int n = 0;
					while (blob.RemainingBytes > 0)
					{
						int b = blob.ReadByte();
						for (int bit = 1; bit < 256; bit <<= 1)
						{
							extraTypeInfo.DynamicFlags[n++] = (b & bit) != 0;
						}
					}
				}
				if (extraTypeInfo.TupleElementNames != null && extraTypeInfo.DynamicFlags != null)
				{
					break;
				}
			}
			return extraTypeInfo.TupleElementNames != null || extraTypeInfo.DynamicFlags != null;
		}
	}

	// --- Applying patches (git-style unified diffs; context must match exactly) ---

	static void ApplyAll(string patchDir, string root)
	{
		if (!Directory.Exists(patchDir))
		{
			return;
		}
		foreach (string patch in Directory.GetFiles(patchDir, "*.patch", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
		{
			Apply(patch, root);
		}
	}

	static string[] Lines(string text)
	{
		return text.Replace("\r\n", "\n").Split('\n');
	}

	static void Apply(string patchFile, string root)
	{
		string[] patch = Lines(File.ReadAllText(patchFile));
		string oldPath = null, newPath = null;
		int i = 0;
		for (; i < patch.Length && !patch[i].StartsWith("@@"); i++)
		{
			if (patch[i].StartsWith("--- "))
			{
				oldPath = patch[i].Substring(4) == "/dev/null" ? null : patch[i].Substring(6);
			}
			else if (patch[i].StartsWith("+++ "))
			{
				newPath = patch[i].Substring(4) == "/dev/null" ? null : patch[i].Substring(6);
			}
		}
		string target = Path.Combine(root, oldPath ?? newPath);
		bool bom = false, crlf = true;
		var lines = new List<string>();
		if (oldPath != null)
		{
			byte[] bytes = File.ReadAllBytes(target);
			bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
			string text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
			crlf = text.Contains("\r\n") || !text.Contains('\n');
			lines.AddRange(Lines(text));
		}
		if (newPath == null)
		{
			File.Delete(target);
			return;
		}
		int offset = 0;
		var header = new Regex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
		while (i < patch.Length)
		{
			Match m = header.Match(patch[i]);
			if (!m.Success)
			{
				i++;
				continue;
			}
			int oldStart = int.Parse(m.Groups[1].Value);
			int oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
			int pos = (oldCount == 0 ? oldStart : oldStart - 1) + offset;
			var removed = new List<string>();
			var added = new List<string>();
			for (i++; i < patch.Length && !patch[i].StartsWith("@@"); i++)
			{
				string l = patch[i];
				if (l.Length == 0 && i == patch.Length - 1 || l.StartsWith("\\"))
				{
					continue;
				}
				char op = l.Length == 0 ? ' ' : l[0];
				string body = l.Length == 0 ? "" : l.Substring(1);
				if (op != '+')
				{
					removed.Add(body);
				}
				if (op != '-')
				{
					added.Add(body);
				}
			}
			for (int k = 0; k < removed.Count; k++)
			{
				if (pos + k >= lines.Count || lines[pos + k] != removed[k])
				{
					throw new BuildException("Patch " + patchFile + " doesn't fit " + target + " at line " + (pos + k + 1) + ".");
				}
			}
			lines.RemoveRange(pos, removed.Count);
			lines.InsertRange(pos, added);
			offset += added.Count - removed.Count;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(target));
		string result = string.Join(crlf ? "\r\n" : "\n", lines);
		File.WriteAllBytes(target, new UTF8Encoding(bom).GetPreamble().Concat(Encoding.UTF8.GetBytes(result)).ToArray());
	}

	static void CopyTree(string from, string to)
	{
		foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
		{
			string dst = Path.Combine(to, Path.GetRelativePath(from, f));
			Directory.CreateDirectory(Path.GetDirectoryName(dst));
			File.Copy(f, dst, true);
		}
	}

	// --- FNA ---

	// HttpClient's default 100 s timeout would cut the VitaSDK download on a slow line.
	static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

	static byte[] Download(string url, string cache)
	{
		string file = Path.Combine(cache, Sha256(Encoding.UTF8.GetBytes(url)).Substring(0, 16) + "-" + Path.GetFileName(new Uri(url).AbsolutePath));
		if (!File.Exists(file))
		{
			Directory.CreateDirectory(cache);
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					File.WriteAllBytes(file + ".part", Http.GetByteArrayAsync(url).GetAwaiter().GetResult());
					break;
				}
				catch (Exception e) when (e is HttpRequestException or TaskCanceledException && attempt < 4)
				{
					Console.WriteLine("   download failed (" + e.Message + "), retrying");
					System.Threading.Thread.Sleep(2000 * attempt);
				}
			}
			File.Move(file + ".part", file, true);
		}
		return File.ReadAllBytes(file);
	}

	static string FetchFna(string dir, string cache)
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
		JsonElement fna = doc.RootElement.GetProperty("fna");
		string root = fna.GetProperty("root").GetString() + "/";
		using (var zip = new ZipArchive(new MemoryStream(Download(fna.GetProperty("zip").GetString(), cache))))
		{
			foreach (ZipArchiveEntry e in zip.Entries)
			{
				if (e.FullName.StartsWith(root) && e.Name != "")
				{
					string dst = Path.Combine(dir, e.FullName.Substring(root.Length));
					Directory.CreateDirectory(Path.GetDirectoryName(dst));
					e.ExtractToFile(dst, true);
				}
			}
		}
		foreach (JsonElement f in fna.GetProperty("files").EnumerateArray())
		{
			string text = Encoding.UTF8.GetString(Download(f.GetProperty("url").GetString(), cache)).Replace("\r\n", "\n");
			if (Sha256(Encoding.UTF8.GetBytes(text)) != f.GetProperty("sha256").GetString())
			{
				throw new BuildException("Unexpected content from " + f.GetProperty("url").GetString());
			}
			string dst = Path.Combine(dir, f.GetProperty("path").GetString());
			Directory.CreateDirectory(Path.GetDirectoryName(dst));
			File.WriteAllText(dst, text);
		}
		ApplyAll(Path.Combine(Repo, "patches", "fna"), dir);
		Run("dotnet", "build", Path.Combine(dir, "FNA.NetFramework.csproj"), "-c", "Release", "-v", "q", "-nologo");
		return Path.Combine(dir, "bin", "Release", "net4.0", "FNA.dll");
	}

	// --- Building ---

	static void Run(string exe, params string[] args)
	{
		var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		using Process p = Process.Start(psi);
		p.WaitForExit();
		if (p.ExitCode != 0)
		{
			throw new BuildException(exe + " " + string.Join(" ", args) + " failed (exit code " + p.ExitCode + ").");
		}
	}

	static void Build(string tree, string game, string outDir, bool saveSerializers)
	{
		string proj = Path.Combine(tree, "Stardew-Valley.csproj");
		string gameDir = "-p:GameDir=" + game.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
		string serializers = Path.Combine(tree, "StardewValley.SaveSerialization", "Generated", "XmlSerializers.cs");
		Directory.CreateDirectory(Path.GetDirectoryName(serializers));
		if (OperatingSystem.IsWindows())
		{
			// The serializer generator reads a built game, so the first build gets an empty contract.
			File.WriteAllText(serializers, "namespace Microsoft.Xml.Serialization.GeneratedAssembly { public class XmlSerializerContract : System.Xml.Serialization.XmlSerializerImplementation { } }\n");
			Step("Building the game (1/2)");
			Run("dotnet", "build", proj, "-c", "Release", "-v", "q", "-nologo", gameDir);
			Step("Generating XML serializers");
			string gen = Path.Combine(tree, "Tools", "SerializerGen");
			Run("dotnet", "build", Path.Combine(gen, "SerializerGen.csproj"), "-c", "Release", "-v", "q", "-nologo");
			Run(Path.Combine(gen, "bin", "Release", "net48", "SerializerGen.exe"), Path.Combine(tree, "bin", "Release", "net40"), serializers);
			CheckSerializerPack(File.ReadAllBytes(serializers), game, saveSerializers);
		}
		else
		{
			// SerializerGen needs .NET Framework's code generator (.NET's writes different code): elsewhere the
			// builder takes its output from the pack a Windows build made.
			Step("Unpacking the XML serializers");
			File.WriteAllBytes(serializers, OpenSerializerPack(game));
		}
		Step("Building the game (2/2)");
		Run("dotnet", "build", proj, "-c", "Release", "-v", "q", "-nologo", gameDir, "-p:VitaRelease=true");
		// FNA.dll goes into the VPK, everything else into ux0:data/StardewValley (Stage).
		Directory.CreateDirectory(outDir);
		File.Copy(Path.Combine(tree, "bin", "Release", "net40", "FNA.dll"), Path.Combine(outDir, "FNA.dll"), true);
	}

	// --- The XML serializer packs (assets/xmlserializers*.bin) for builds outside Windows ---
	//
	// SerializerGen's output for the current patches, made on Windows with --save-serializers. It is derived from
	// the game's types, so it is encrypted with a key from the player's own Stardew Valley.exe: only the verified
	// game opens it. Format: "SVXS1\0\0\0", the inputs' SHA-256 (InputsHash), a 16-byte IV, the AES-256-CBC
	// encrypted deflated file, then HMAC-SHA256 over everything before it.

	// One pack per known build (each is keyed to its own exe): known-games.json "serializers".
	static string SerializerPackPath => Path.Combine(Repo, "assets", GameBuild.SerializerPack);

	static readonly byte[] PackMagic = Encoding.ASCII.GetBytes("SVXS1\0\0\0");

	// What SerializerGen's output depends on besides the game: our patches and files and the FNA sources. Line
	// endings don't count (a git checkout may have either).
	static byte[] InputsHash()
	{
		var text = new StringBuilder();
		foreach (string top in new[] { "patches", "overlay" })
		{
			string root = Path.Combine(Repo, top);
			foreach (string rel in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
				.Select(f => top + "/" + Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
			{
				byte[] data = File.ReadAllBytes(Path.Combine(Repo, rel));
				string normalized = Encoding.Latin1.GetString(data).Replace("\r\n", "\n");
				text.Append(rel).Append(' ').Append(Sha256(Encoding.Latin1.GetBytes(normalized))).Append('\n');
			}
		}
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
		text.Append(doc.RootElement.GetProperty("fna").GetRawText().Replace("\r\n", "\n"));
		return SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
	}

	static byte[] PackKey(string game, string purpose)
	{
		byte[] exe = SHA256.HashData(File.ReadAllBytes(Path.Combine(game, "Stardew Valley.exe")));
		return HMACSHA256.HashData(exe, Encoding.ASCII.GetBytes("stardew-vita xmlserializers " + purpose));
	}

	// Windows: compares the pack with what SerializerGen just wrote, or writes it (--save-serializers).
	static void CheckSerializerPack(byte[] generated, string game, bool save)
	{
		byte[] inputs = InputsHash();
		if (save)
		{
			var deflated = new MemoryStream();
			using (var z = new DeflateStream(deflated, CompressionLevel.SmallestSize, true))
			{
				z.Write(generated);
			}
			using var aes = Aes.Create();
			aes.Key = PackKey(game, "aes");
			byte[] iv = RandomNumberGenerator.GetBytes(16);
			byte[] body = PackMagic.Concat(inputs).Concat(iv).Concat(aes.EncryptCbc(deflated.ToArray(), iv)).ToArray();
			File.WriteAllBytes(SerializerPackPath, body.Concat(HMACSHA256.HashData(PackKey(game, "mac"), body)).ToArray());
			Console.WriteLine("   wrote " + SerializerPackPath);
			return;
		}
		if (!File.Exists(SerializerPackPath) || !File.ReadAllBytes(SerializerPackPath).AsSpan(8, 32).SequenceEqual(inputs))
		{
			Console.WriteLine("   note: assets/" + GameBuild.SerializerPack + " is missing or older than the patches; builds outside Windows need it"
				+ " (maintainers: run the builder with --save-serializers)");
		}
		else if (!OpenSerializerPack(game).AsSpan().SequenceEqual(generated))
		{
			throw new BuildException("assets/" + GameBuild.SerializerPack + " doesn't match SerializerGen's output for the same patches.");
		}
	}

	static byte[] OpenSerializerPack(string game)
	{
		if (!File.Exists(SerializerPackPath))
		{
			throw new BuildException("assets/" + GameBuild.SerializerPack + " is missing: outside Windows the builder needs it.");
		}
		byte[] pack = File.ReadAllBytes(SerializerPackPath);
		int body = pack.Length - 32;
		if (pack.Length < 8 + 32 + 16 + 32 || !pack.AsSpan(0, 8).SequenceEqual(PackMagic)
			|| !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(PackKey(game, "mac"), pack.AsSpan(0, body)), pack.AsSpan(body)))
		{
			throw new BuildException("assets/" + GameBuild.SerializerPack + " doesn't open with this game: it is damaged.");
		}
		if (!pack.AsSpan(8, 32).SequenceEqual(InputsHash()))
		{
			throw new BuildException("assets/" + GameBuild.SerializerPack + " was made for other patches; it needs to be made again on Windows (--save-serializers).");
		}
		using var aes = Aes.Create();
		aes.Key = PackKey(game, "aes");
		byte[] deflated = aes.DecryptCbc(pack.AsSpan(56, body - 56), pack.AsSpan(40, 16));
		using var z = new DeflateStream(new MemoryStream(deflated), CompressionMode.Decompress);
		var result = new MemoryStream();
		z.CopyTo(result);
		return result.ToArray();
	}

	// --- ux0:data/StardewValley: our assemblies plus the player's Content ---

	static readonly string[] GameAssemblies =
	{
		"Stardew Valley.exe", "StardewValley.GameData.dll", "xTile.dll", "BmFont.dll", "Lidgren.Network.dll",
		"System.Data.HashFunction.Core.dll", "System.Data.HashFunction.Interfaces.dll", "System.Data.HashFunction.xxHash.dll"
	};

	static readonly string[] WaveBanks = { "Wave Bank.xwb", "Wave Bank(1.4).xwb" };

	static void Stage(string tree, string game, string fna, string target, string sceSys, string cache)
	{
		Step("Staging ux0:data/StardewValley");
		if (Directory.Exists(target))
		{
			Directory.Delete(target, true);
		}
		Directory.CreateDirectory(target);
		foreach (string name in GameAssemblies)
		{
			File.Copy(Path.Combine(tree, "bin", "Release", "net40", name), Path.Combine(target, name));
		}
		File.Copy(Path.Combine(Repo, "assets", "vita-controls.png"), Path.Combine(target, "vita-controls.png"));
		File.Copy(Path.Combine(tree, "Tools", "Vita", "stardew", "vita-prejit.txt"), Path.Combine(target, "vita-prejit.txt"));
		string content = Path.Combine(target, "Content");
		CopyTree(Path.Combine(game, "Content"), content);
		CopyTree(Path.Combine(tree, "Content"), content);
		// In-memory wave banks would need ~475 MB of RAM; the Vita streams them from converted banks.
		string tool = Path.Combine(tree, "Tools", "WaveBankTool");
		Run("dotnet", "build", Path.Combine(tool, "WaveBankTool.csproj"), "-c", "Release", "-v", "q", "-nologo");
		foreach (string bank in WaveBanks)
		{
			string source = Path.Combine(game, "Content", "XACT", bank);
			string cached = Path.Combine(cache, Sha256(File.ReadAllBytes(source)).Substring(0, 16) + "-" + bank);
			if (!File.Exists(cached))
			{
				Step("Converting " + bank + " to a streaming bank");
				Directory.CreateDirectory(cache);
				Run(Path.Combine(tool, "bin", "Release", "net8.0", OperatingSystem.IsWindows() ? "WaveBankTool.exe" : "WaveBankTool"), "streamify", source, cached + ".part");
				File.Move(cached + ".part", cached, true);
			}
			File.Copy(cached, Path.Combine(content, "XACT", bank), true);
		}
		Step("Packing Content.pak");
		var replaced = new Dictionary<string, byte[]>();
		foreach ((string key, string sha) in PrebakedTextures)
		{
			byte[] xnb = File.ReadAllBytes(Path.Combine(content, key + ".xnb"));
			if (Sha256(xnb) != sha)
			{
				throw new BuildException("Unexpected " + key + ".xnb");
			}
			replaced[key.ToLowerInvariant()] = PrebakeBc3(xnb, fna);
		}
		WriteContentPack(content, Path.Combine(target, "Content.pak"), replaced);
		// The settings every test ran with: the launcher reads these at start (main.c set_clocks/apply_gc_params);
		// without them it asks for 500 MHz and Mono's default 4 MB nursery.
		File.WriteAllText(Path.Combine(target, "vita-cpu-mhz"), "444\n");
		File.WriteAllText(Path.Combine(target, "gc-params.txt"), "nursery-size=16m\n");
		long size = Directory.GetFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
		Console.WriteLine("   {0:N0} MB", size / 1048576);
		Step("Drawing the LiveArea, icon and manual");
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
		JsonElement pegi = doc.RootElement.GetProperty("pegi12");
		byte[] label = Download(pegi.GetProperty("url").GetString(), cache);
		if (Sha256(label) != pegi.GetProperty("sha256").GetString())
		{
			throw new BuildException("Unexpected content from " + pegi.GetProperty("url").GetString());
		}
		if (Directory.Exists(sceSys))
		{
			Directory.Delete(sceSys, true);
		}
		string labelPath = Path.Combine(cache, "pegi-12.png");
		File.WriteAllBytes(labelPath, label);
		LiveArea.Write(content, fna, Repo, labelPath, sceSys);
	}

	// Every .xnb in one file (StardewValley/VitaContentPack.cs): opening a file on the memory card costs ~100 ms.
	// Format (little endian): "SVPK1\0\0\0", u32 count, then per entry u16 name length, name (UTF-8, path under
	// Content without ".xnb", "/" separated, lower case), u64 offset, u32 length; then the data.
	// Two large static map tile sheets go into the pack as BC3 instead of raw RGBA (2 MB -> 0.5 MB of texture
	// memory each); the loose files stay original. The keys pin the source files.
	static readonly (string Key, string Sha256)[] PrebakedTextures =
	{
		("Maps/spring_outdoorsTileSheet", "170b3c3d4adb8f0b2954b86caca9611a115d8232ac795fe0752c62381bf795b9"),
		("Maps/summer_outdoorsTileSheet", "c78a286270c48c972bb6d094c902e98111c27a03168e58d4a5c1a9ea16d82d18"),
	};

	internal static int Read7BitInt(BinaryReader r)
	{
		int v = 0;
		for (int shift = 0; shift < 32; shift += 7)
		{
			byte b = r.ReadByte();
			v |= (b & 127) << shift;
			if ((b & 128) == 0)
			{
				return v;
			}
		}
		throw new InvalidDataException("bad 7-bit int");
	}

	// The XNB body after LZX decompression, with FNA's own decoder (internal, so through reflection).
	internal static byte[] XnbBody(byte[] xnb, string fnaPath)
	{
		if ((xnb[5] & 0x80) == 0)
		{
			return xnb[10..];
		}
		int expected = BitConverter.ToInt32(xnb, 10);
		var asm = System.Reflection.Assembly.LoadFrom(fnaPath);
		Type type = asm.GetType("Microsoft.Xna.Framework.Content.LzxDecoder", true);
		const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
		object decoder = Activator.CreateInstance(type, any, null, new object[] { 16 }, null);
		var decompress = type.GetMethod("Decompress", any);
		var src = new MemoryStream(xnb, 14, xnb.Length - 14);
		var dst = new MemoryStream(expected);
		long pos = 0;
		while (pos < src.Length)
		{
			src.Position = pos;
			int hi = src.ReadByte(), lo = src.ReadByte();
			int block = (hi << 8) | lo, frame = 32768;
			if (hi == 0xFF)
			{
				hi = lo;
				lo = src.ReadByte();
				frame = (hi << 8) | lo;
				hi = src.ReadByte();
				lo = src.ReadByte();
				block = (hi << 8) | lo;
				pos += 5;
			}
			else
			{
				pos += 2;
			}
			if (block == 0 || frame == 0)
			{
				break;
			}
			decompress.Invoke(decoder, new object[] { src, block, dst, frame });
			pos += block;
		}
		if (dst.Length != expected)
		{
			throw new InvalidDataException("LZX output size mismatch");
		}
		return dst.ToArray();
	}

	// A single-mip Color Texture2D XNB rewritten as an uncompressed Dxt5 XNB.
	static byte[] PrebakeBc3(byte[] xnb, string fnaPath)
	{
		byte[] body = XnbBody(xnb, fnaPath);
		using var ms = new MemoryStream(body);
		using var r = new BinaryReader(ms);
		int readers = Read7BitInt(r);
		string rootReader = null;
		var names = new string[readers];
		for (int i = 0; i < readers; i++)
		{
			names[i] = Encoding.UTF8.GetString(r.ReadBytes(Read7BitInt(r)));
			r.ReadInt32();
		}
		int shared = Read7BitInt(r);
		int root = Read7BitInt(r);
		rootReader = root >= 1 && root <= readers ? names[root - 1] : "";
		int texStart = (int)ms.Position;
		int format = r.ReadInt32(), width = r.ReadInt32(), height = r.ReadInt32(), levels = r.ReadInt32();
		byte[] level0 = r.ReadBytes(r.ReadInt32());
		if (shared != 0 || !rootReader.Contains("Texture2DReader") || format != 0 || levels != 1 || width % 4 != 0 || height % 4 != 0
			|| level0.Length != width * height * 4 || ms.Position != body.Length)
		{
			throw new InvalidDataException("not a single-mip Color texture");
		}
		byte[] coded = StbDxt.EncodeBc3(level0, width, height);
		var outBody = new MemoryStream();
		var w = new BinaryWriter(outBody);
		w.Write(body, 0, texStart);
		w.Write(6);
		w.Write(width);
		w.Write(height);
		w.Write(1);
		w.Write(coded.Length);
		w.Write(coded);
		w.Flush();
		var result = new MemoryStream();
		var x = new BinaryWriter(result);
		x.Write(Encoding.ASCII.GetBytes("XNBw"));
		x.Write((byte)5);
		x.Write((byte)0);
		x.Write((uint)(10 + outBody.Length));
		x.Write(outBody.ToArray());
		x.Flush();
		return result.ToArray();
	}

	static void WriteContentPack(string content, string output, Dictionary<string, byte[]> replaced)
	{
		var files = Directory.GetFiles(content, "*.xnb", SearchOption.AllDirectories)
			.Select(p => (Key: Path.GetRelativePath(content, p)[..^4].Replace('\\', '/').ToLowerInvariant(), Path: p))
			.OrderBy(e => e.Key, StringComparer.Ordinal)
			.ToList();
		long offset = 12 + files.Sum(e => 2 + Encoding.UTF8.GetByteCount(e.Key) + 12);
		using var stream = new FileStream(output, FileMode.Create);
		using var writer = new BinaryWriter(stream);
		writer.Write(Encoding.ASCII.GetBytes("SVPK1\0\0\0"));
		writer.Write(files.Count);
		foreach (var e in files)
		{
			byte[] name = Encoding.UTF8.GetBytes(e.Key);
			long length = replaced.TryGetValue(e.Key, out byte[] data) ? data.Length : new FileInfo(e.Path).Length;
			writer.Write((ushort)name.Length);
			writer.Write(name);
			writer.Write(offset);
			writer.Write((uint)length);
			offset += length;
		}
		foreach (var e in files)
		{
			writer.Write(replaced.TryGetValue(e.Key, out byte[] data) ? data : File.ReadAllBytes(e.Path));
		}
	}
}
