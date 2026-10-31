// The second half of the build, on Windows or Linux without WSL or a Vita setup: AOT-compiles the assemblies just
// built with the cross compiler from our toolchain zip for the system (vpk/pack-toolchain-zip.sh), links them with
// the launcher's objects into the eboot using the official VitaSDK (downloaded, tools/sources.json), and packs
// stardew.vpk. It does what overlay/game/Tools/Vita/aot-stardew.sh and build-stardew.sh (stardew/CMakeLists.txt) do
// in WSL, with the same options; the eboot is the same as theirs but for Mono's random AOT ids and the launcher's
// build time.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

static partial class Program
{
	// aot-stardew.sh's default list: the runtime's and the game's assemblies, without FNA and xTile (drawing code
	// runs faster JIT-compiled); with the profile, only the profiled methods of these three are precompiled.
	static readonly string[] AotAssemblies = { "mscorlib", "System", "System.Core", "System.Xml", "StardewValley.GameData", "BmFont", "Stardew Valley" };
	static readonly string[] ProfileOnly = { "System", "System.Xml", "Stardew Valley" };

	static readonly string[] ClassLibraries = { "mscorlib", "System", "System.Core", "System.Xml", "System.Numerics", "System.Configuration", "System.Security", "Mono.Security" };

	static readonly string[] LauncherObjects =
	{
		"main.c.obj", "vita_sdl3_table.c.obj", "vita_fna3d_table.c.obj", "vita_faudio_table.c.obj",
		"vita_sdl_overrides.c.obj", "vita_gl_glue.c.obj", "vita_glsl_rewrite.c.obj", "vita_aot_modules.c.obj"
	};

	// stardew/CMakeLists.txt: the file system calls main.c redirects, and the libraries in its order.
	static readonly string[] LinkOptions =
	{
		"-Wl,-q", "-O3", "-DNDEBUG", "-Wl,-z,nocopyreloc", "-Wl,--no-enum-size-warning", "-Wl,--defsym,__sce_headroom=0x10000",
		"-Wl,--wrap=open,--wrap=fopen,--wrap=stat,--wrap=lstat,--wrap=access,--wrap=mkdir,--wrap=rmdir,--wrap=unlink,--wrap=remove,--wrap=rename,--wrap=opendir,--wrap=chdir,--wrap=chmod,--wrap=truncate,--wrap=utime,--wrap=utimes,--wrap=realpath,--wrap=getcwd,--wrap=mono_threads_state_poll"
	};

	static readonly string LinkLibraries =
		"-lFNA3D -lmojoshader -lFAudio -lSDL3 -lvitaGL -lvitashark -lSceShaccCgExt -lmathneon -ltaihen_stub -lSceShaccCg_stub -lSceGxm_stub " +
		"-lSceDisplay_stub -lSceCtrl_stub -lSceAppUtil_stub -lSceCommonDialog_stub -lSceIme_stub -lSceAudio_stub -lSceAudioIn_stub -lSceHid_stub " +
		"-lSceMotion_stub -lSceTouch_stub -lSceVshBridge_stub -lScePower_stub -lSceCamera_stub -lSceNpTrophy_stub -lSceAppMgr_stub " +
		"-lstdc++ -lvitanative -lmono-profiler-aot -lvita-sampler -lmonosgen-2.0 -lvitashim -lkubridge_stub_weak -lSceKernelDmacMgr_stub " +
		"-lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceSysmodule_stub -lSceAppMgr_stub -lSceRtc_stub -lz -lpthread -lm -lc " +
		"-lstdc++ -lvitanative -lmono-profiler-aot -lvita-sampler -lmonosgen-2.0 -lvitashim -lkubridge_stub_weak -lSceKernelDmacMgr_stub " +
		"-lSceLibKernel_stub -lSceKernelThreadMgr_stub -lSceSysmodule_stub -lSceRtc_stub -lz -lpthread -lm -lc";

	const string TitleId = "STDV00001";
	const string AppVer = "01.60";
	const string VpkTitle = "Stardew Valley Enhanced";

	// The system the tools are for: "windows" or "linux" (x64), the keys of tools/sources.json's vitasdk.core and
	// toolchain. macOS has no toolchain yet: there the builder runs in a Linux container (tools/docker-build.sh).
	static string Host()
	{
		// Windows on ARM runs the x64 tools emulated.
		if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64)
		{
			return "windows";
		}
		if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
		{
			return "linux";
		}
		throw new BuildException("There is no Vita toolchain for " + RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture
			+ ") yet. Build in a Linux container with tools/docker-build.sh (Docker), or pass --no-vpk to stop before the VPK.");
	}

	static string Exe(string path)
	{
		return OperatingSystem.IsWindows() ? path + ".exe" : path;
	}

	// Where the native tools work. Windows' tools (tar, gcc, the Mono compiler) take paths in the ANSI code page and
	// gcc's headers sit ~130 characters deep, so on Windows the folder has to be short and plain ASCII: a work folder
	// under a user name like "Çağrı" moves them to C:\ProgramData\StardewVitaBuilder.
	static string NativeRoot(string work, string arg)
	{
		static bool Plain(string path) => path.All(c => c >= 32 && c < 127) && path.Length <= 100;
		if (arg != null)
		{
			string dir = Path.GetFullPath(arg);
			if (OperatingSystem.IsWindows() && !Plain(dir))
			{
				throw new BuildException("--native-dir needs a short path of plain ASCII letters (like C:\\svb): " + dir);
			}
			return dir;
		}
		if (!OperatingSystem.IsWindows() || Plain(work))
		{
			return work;
		}
		string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "StardewVitaBuilder");
		if (!Plain(root))
		{
			throw new BuildException("The work folder's path is too long or has non-ASCII letters, which the Vita tools can't use: "
				+ work + "\nPass --native-dir with a short plain path, like --native-dir C:\\svb.");
		}
		Console.WriteLine("   the Vita tools work in " + root + " (the work folder's path is too long or not plain ASCII)");
		return root;
	}

	static void MakeVpk(string work, string outDir, string fna, string toolchainZip, string nativeDir)
	{
		string host = Host();
		string native = NativeRoot(work, nativeDir);
		string cache = Path.Combine(native, "downloads");
		string sdk = FetchVitaSdk(Path.Combine(native, "vitasdk"), cache, host);
		string tc = FetchToolchain(Path.Combine(native, "toolchain"), cache, toolchainZip, host);
		string game = GameData(outDir);
		string build = Path.Combine(native, "vpk");
		if (Directory.Exists(build))
		{
			Directory.Delete(build, true);
		}
		string libs = Path.Combine(build, "assemblies");
		Directory.CreateDirectory(libs);
		// One folder with exactly the files the device loads: Mono uses an image only for the assembly it was
		// compiled from.
		foreach (string dll in ClassLibraries)
		{
			File.Copy(Path.Combine(tc, "mono", "lib", "mono", "4.5", dll + ".dll"), Path.Combine(libs, dll + ".dll"));
		}
		File.Copy(fna, Path.Combine(libs, "FNA.dll"));
		foreach (string f in Directory.GetFiles(game, "*.dll").Concat(Directory.GetFiles(game, "*.exe")))
		{
			File.Copy(f, Path.Combine(libs, Path.GetFileName(f)));
		}
		// The VPK's art too: every path the tools see is under the native folder.
		string sceSys = Path.Combine(build, "sce_sys");
		CopyTree(Path.Combine(outDir, "sce_sys"), sceSys);

		string bin = Path.Combine(sdk, "bin");
		var env = new Dictionary<string, string> { ["VITASDK"] = sdk, ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") };
		string gcc = Exe(Path.Combine(bin, "arm-vita-eabi-gcc"));
		var objects = new List<string>();
		foreach (string name in AotAssemblies)
		{
			string file = Path.Combine(libs, name + ".dll");
			if (!File.Exists(file))
			{
				file = Path.Combine(libs, name + ".exe");
			}
			string baseName = Path.Combine(build, name.Replace(' ', '_'));
			string profile = ProfileOnly.Contains(name) ? ",profile=" + Path.Combine(tc, "game.aotprofile") + ",profile-only" : "";
			var watch = Stopwatch.StartNew();
			// explicit-null-checks: the Vita gives no SIGSEGV for null references. MONO_GC_PARAMS: the compiler is
			// built without the concurrent collector, like the Vita runtime.
			RunLogged(Exe(Path.Combine(tc, "mono", "bin", "mono-sgen")), baseName + ".log", build,
				new Dictionary<string, string> { ["MONO_PATH"] = libs, ["MONO_DEBUG"] = "explicit-null-checks", ["MONO_GC_PARAMS"] = "major=marksweep" },
				"--aot=static,asmonly,nodebug,mtriple=armv7-vita-eabi,outfile=" + baseName + ".s" + profile, file);
			RunLogged(gcc, baseName + ".as.log", build, env, "-c", baseName + ".s", "-o", baseName + ".o");
			File.Delete(baseName + ".s");
			objects.Add(baseName + ".o");
			string compiled = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(baseName + ".log"), @"Compiled: \d+/\d+").Value;
			Console.WriteLine("   {0,-24} {1,7:N0} KB {2,4} s  {3}", name, new FileInfo(baseName + ".o").Length / 1024, (int)watch.Elapsed.TotalSeconds, compiled);
		}

		Step("Linking eboot.bin");
		var link = new List<string>(LinkOptions);
		// In CMakeLists.txt's order (its file glob is sorted by name), so the eboot's layout is the same.
		link.AddRange(objects.OrderBy(Path.GetFileName, StringComparer.Ordinal));
		link.AddRange(LauncherObjects.Select(o => Path.Combine(tc, "launcher", o)));
		link.AddRange(new[] { "-o", Path.Combine(build, "stardew"), "-L" + Path.Combine(tc, "lib") });
		link.AddRange(LinkLibraries.Split(' '));
		RunLogged(gcc, Path.Combine(build, "link.log"), build, env, link.ToArray());
		// Relative names: vita-elf-create takes the module name from the file name, cut at the last '/' only.
		RunLogged(Exe(Path.Combine(bin, "vita-elf-create")), Path.Combine(build, "elf-create.log"), build, env, "stardew", "stardew.velf");
		RunLogged(Exe(Path.Combine(bin, "vita-make-fself")), Path.Combine(build, "fself.log"), build, env, "-c", Path.Combine(build, "stardew.velf"), Path.Combine(build, "eboot.bin"));
		RunLogged(Exe(Path.Combine(bin, "vita-mksfoex")), Path.Combine(build, "sfo.log"), build, env,
			"-d", "ATTRIBUTE2=12", "-s", "APP_VER=" + AppVer, "-s", "TITLE_ID=" + TitleId, VpkTitle, Path.Combine(build, "param.sfo"));

		Step("Packing stardew.vpk");
		var pack = new List<string>();
		void Add(string source, string path)
		{
			pack.Add("-a");
			pack.Add(source + "=" + path);
		}
		Add(Path.Combine(tc, "mono", "etc", "mono", "4.5", "machine.config"), "etc/mono/4.5/machine.config");
		Add(Path.Combine(tc, "mono", "etc", "mono", "config"), "etc/mono/config");
		Add(Path.Combine(libs, "FNA.dll"), "lib/fna/FNA.dll");
		foreach (string dll in ClassLibraries.OrderBy(d => d, StringComparer.Ordinal))
		{
			Add(Path.Combine(tc, "mono", "lib", "mono", "4.5", dll + ".dll"), "lib/mono/4.5/" + dll + ".dll");
		}
		foreach (string rel in Directory.GetFiles(sceSys, "*", SearchOption.AllDirectories)
			.Select(f => Path.GetRelativePath(sceSys, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
		{
			Add(Path.Combine(sceSys, rel), "sce_sys/" + rel);
		}
		pack.AddRange(new[] { "-s", Path.Combine(build, "param.sfo"), "-b", Path.Combine(build, "eboot.bin"), Path.Combine(build, "stardew.vpk") });
		RunLogged(Exe(Path.Combine(bin, "vita-pack-vpk")), Path.Combine(build, "pack.log"), build, env, pack.ToArray());
		string vpk = Path.Combine(outDir, "ux0", "stardew.vpk");
		File.Copy(Path.Combine(build, "stardew.vpk"), vpk + ".part", true);
		File.Move(vpk + ".part", vpk, true);
		Console.WriteLine("   {0}: {1:N0} bytes, SHA-256 {2}", vpk, new FileInfo(vpk).Length, Sha256(File.ReadAllBytes(vpk)));
	}

	// The VitaSDK packages, checked against tools/sources.json and unpacked with the system's tar (Windows 10 1803
	// and newer have one that reads .xz).
	static string FetchVitaSdk(string dir, string cache, string host)
	{
		using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
		JsonElement vitasdk = doc.RootElement.GetProperty("vitasdk");
		var packages = new[] { vitasdk.GetProperty("core").GetProperty(host) }.Concat(vitasdk.GetProperty("packages").EnumerateArray()).ToList();
		string stamp = Path.Combine(dir, ".packages");
		string expected = string.Join("\n", packages.Select(p => p.GetProperty("sha256").GetString()));
		if (File.Exists(stamp) && File.ReadAllText(stamp) == expected)
		{
			return dir;
		}
		Step("Fetching VitaSDK (official release, ~" + (host == "windows" ? 70 : 45) + " MB once)");
		if (Directory.Exists(dir))
		{
			Directory.Delete(dir, true);
		}
		Directory.CreateDirectory(dir);
		string tar = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "tar.exe") : "tar";
		if (OperatingSystem.IsWindows() && !File.Exists(tar))
		{
			throw new BuildException("Windows' tar.exe is missing (Windows 10 1803 or newer has it): " + tar);
		}
		string log = Path.Combine(cache, "tar.log");
		foreach (JsonElement p in packages)
		{
			string url = p.GetProperty("url").GetString();
			byte[] data = Download(url, cache);
			if (Sha256(data) != p.GetProperty("sha256").GetString())
			{
				throw new BuildException("Unexpected content from " + url);
			}
			string file = Path.Combine(cache, Sha256(Encoding.UTF8.GetBytes(url)).Substring(0, 16) + "-" + Path.GetFileName(new Uri(url).AbsolutePath));
			RunLogged(tar, log, dir, new Dictionary<string, string>(), "-xf", file, "--exclude", ".BUILDINFO", "--exclude", ".MTREE", "--exclude", ".PKGINFO");
		}
		File.WriteAllText(stamp, expected);
		return dir;
	}

	// Before the long build, so a wrong, missing or damaged zip stops it at once.
	static void CheckToolchain(string zipPath)
	{
		string host = Host();
		if (zipPath == null)
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
			if (string.IsNullOrEmpty(doc.RootElement.GetProperty("toolchain").GetProperty(host).GetProperty("url").GetString()))
			{
				throw new BuildException("vita-toolchain-" + host + ".zip not found: put it in this folder (next to builder) or pass --toolchain <zip>.");
			}
			return;
		}
		if (!File.Exists(zipPath))
		{
			throw new BuildException("No such file: " + Path.GetFullPath(zipPath));
		}
		try
		{
			using var zip = ZipFile.OpenRead(zipPath);
			CheckToolchainZip(zip, host);
		}
		catch (InvalidDataException e)
		{
			throw new BuildException("The toolchain zip is damaged (" + e.Message + "); download it again.");
		}
	}

	static void CheckToolchainZip(ZipArchive zip, string host)
	{
		if (zip.GetEntry("mono/bin/" + Exe("mono-sgen")) == null)
		{
			throw new BuildException("This toolchain zip isn't for " + host + " (no mono/bin/" + Exe("mono-sgen") + "): use vita-toolchain-" + host + ".zip.");
		}
	}

	static string FetchToolchain(string dir, string cache, string zipPath, string host)
	{
		byte[] data;
		if (zipPath != null)
		{
			if (!File.Exists(zipPath))
			{
				throw new BuildException("No such file: " + Path.GetFullPath(zipPath));
			}
			data = File.ReadAllBytes(zipPath);
		}
		else
		{
			using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, "tools", "sources.json")));
			JsonElement t = doc.RootElement.GetProperty("toolchain").GetProperty(host);
			string url = t.GetProperty("url").GetString();
			if (string.IsNullOrEmpty(url))
			{
				throw new BuildException("vita-toolchain-" + host + ".zip not found: put it in this folder (next to builder) or pass --toolchain <zip>.");
			}
			data = Download(url, cache);
			if (Sha256(data) != t.GetProperty("sha256").GetString())
			{
				throw new BuildException("Unexpected content from " + url);
			}
		}
		string sha = Sha256(data);
		string stamp = Path.Combine(dir, ".sha256");
		if (File.Exists(stamp) && File.ReadAllText(stamp) == sha)
		{
			return dir;
		}
		Step("Unpacking the toolchain");
		if (Directory.Exists(dir))
		{
			Directory.Delete(dir, true);
		}
		try
		{
			using var zip = new ZipArchive(new MemoryStream(data));
			CheckToolchainZip(zip, host);
			zip.ExtractToDirectory(dir);
		}
		catch (InvalidDataException e)
		{
			throw new BuildException("The toolchain zip is damaged (" + e.Message + "); download it again.");
		}
		if (!OperatingSystem.IsWindows())
		{
			foreach (string f in Directory.GetFiles(Path.Combine(dir, "mono", "bin")))
			{
				File.SetUnixFileMode(f, (UnixFileMode)0b111_101_101);
			}
		}
		File.WriteAllText(stamp, sha);
		return dir;
	}

	// Runs a tool with its output in a log file; on failure the log's end is shown.
	static void RunLogged(string exe, string log, string cwd, Dictionary<string, string> env, params string[] args)
	{
		var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
		foreach (string a in args)
		{
			psi.ArgumentList.Add(a);
		}
		foreach (var (k, v) in env)
		{
			psi.Environment[k] = v;
		}
		using (var output = new StreamWriter(log))
		using (Process p = Process.Start(psi))
		{
			var sync = new object();
			p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sync) output.WriteLine(e.Data); };
			p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sync) output.WriteLine(e.Data); };
			p.BeginOutputReadLine();
			p.BeginErrorReadLine();
			p.WaitForExit();
			if (p.ExitCode == 0)
			{
				return;
			}
		}
		foreach (string line in File.ReadLines(log).TakeLast(20))
		{
			Console.Error.WriteLine("   " + line);
		}
		throw new BuildException(Path.GetFileName(exe) + " failed (log: " + log + ").");
	}
}
