// Mono-on-Vita smoke test (milestone 3): the runtime features Stardew Valley leans on, one check at a
// time, each result flushed to a file so a crash still leaves the last line written.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml;

static class Hello
{
	static StreamWriter log;
	static int failures;

	struct Pair
	{
		public int Key;
		public double Value;
	}

	interface IShape
	{
		double Area();
	}

	class Square : IShape
	{
		public double Side;
		public double Area() => Side * Side;
	}

	class Finalizable
	{
		public static int Finalized;
		~Finalizable() { Interlocked.Increment(ref Finalized); }
	}

	static void Check(string name, Func<bool> test)
	{
		var watch = Stopwatch.StartNew();
		bool ok;
		string error = "";
		try
		{
			ok = test();
		}
		catch (Exception e)
		{
			ok = false;
			error = " " + e.GetType().Name + ": " + e.Message + "\n" + e;
		}
		log.WriteLine("{0} {1} ({2} ms){3}", ok ? "ok  " : "FAIL", name, watch.ElapsedMilliseconds, error);
		log.Flush();
		if (!ok)
			failures++;
	}

	static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

	// Bisection aid: can a large-object-space array still be allocated at this point?
	static void Probe(string where)
	{
		string result;
		try
		{
			result = new char[4096].Length == 4096 ? "ok" : "wrong length";
		}
		catch (Exception e)
		{
			result = e.GetType().Name;
		}
		log.WriteLine("     probe after {0}: LOS char[4096] {1}, total memory {2} KB", where, result, GC.GetTotalMemory(false) / 1024);
		log.Flush();
	}

	static T[] MakeArray<T>(int n) => new T[n];

	[ThreadStatic]
	static int threadStatic;

	static class GenericHolder<T>
	{
		[ThreadStatic]
		public static T[][] Value;
	}

	static int Main(string[] args)
	{
		string dir = args.Length > 0 ? args[0] : "ux0:data/monotest";
		log = new StreamWriter(Path.Combine(dir, "managed.txt"));
		log.WriteLine("managed Main: CLR {0}, {1}-bit, {2} processors", Environment.Version, IntPtr.Size * 8, Environment.ProcessorCount);
		log.Flush();

		Check("small arrays", () => new char[1000].Length == 1000 && new byte[7000].Length == 7000);
		Check("large byte[] (LOS)", () => new byte[100000].Length == 100000);
		Check("large char[] (LOS)", () => new char[4096].Length == 4096);
		Check("generic small array", () => MakeArray<char>(100).Length == 100);
		Check("generic large char array", () => MakeArray<char>(4096).Length == 4096);
		Check("generic large byte array", () => MakeArray<byte>(16 << 8).Length == 4096);
		Check("string formatting", () => (1234.5).ToString("F2", CultureInfo.InvariantCulture) == "1234.50" && string.Format("{0}-{1}", "a", 7) == "a-7");
		Check("string builder", () => { var sb = new System.Text.StringBuilder(); for (int i = 0; i < 1000; i++) sb.Append(i % 10); return sb.Length == 1000; });
		Check("generic dictionary", () => { var d = new Dictionary<string, int>(); for (int i = 0; i < 10000; i++) d["k" + i] = i; return d["k9999"] == 9999 && d.Count == 10000; });
		Check("struct generics", () => { var l = new List<Pair>(); for (int i = 0; i < 1000; i++) l.Add(new Pair { Key = i, Value = i * 0.5 }); l.Sort((a, b) => b.Key.CompareTo(a.Key)); return l[0].Key == 999 && l[999].Value == 0; });
		Check("linq", () => Enumerable.Range(1, 100).Where(i => i % 3 == 0).Select(i => i * 2).Sum() == 3366);
		Check("interfaces and boxing", () => { IShape s = new Square { Side = 3 }; object boxed = 5; return s.Area() == 9 && (int)boxed == 5; });
		Check("delegates and closures", () => { int counter = 0; Action add = () => counter++; for (int i = 0; i < 10; i++) add(); Func<int, int> sq = x => x * x; return counter == 10 && sq(7) == 49; });
		Check("exceptions", () => { try { throw new InvalidOperationException("thrown"); } catch (InvalidOperationException e) { return e.Message == "thrown"; } });
		Check("finally", () => { int state = 0; try { try { state = 1; throw new Exception(); } finally { state = 2; } } catch { } return state == 2; });
		Check("null reference", () => { try { string s = null; return s.Length == -1; } catch (NullReferenceException) { return true; } });
		Check("divide by zero", () => { try { int zero = 0; return 1 / zero == 0; } catch (DivideByZeroException) { return true; } });
		Check("array bounds", () => { try { var a = new int[2]; int i = 2; return a[i] == 0; } catch (IndexOutOfRangeException) { return true; } });
		Check("float math", () => Math.Abs(Math.Sin(1.0) - 0.8414709848078965) < 1e-12 && Math.Sqrt(2.0) > 1.414 && (float)Math.PI > 3.14f);
		Check("Math.Round banker's", () => { double d = 2.5; return Math.Round(d) == 2.0 && Math.Round(3.5) == 4.0; });
		Check("long arithmetic", () => { long a = 0x123456789L; return a * 16 == 0x1234567890L && (ulong)a % 1000 == 4886718345UL % 1000; });
		Check("reflection", () => typeof(Hello).GetMethod("Fib", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { 10 }).Equals(55));
		Check("enums", () => DayOfWeek.Friday.ToString() == "Friday" && (DayOfWeek)Enum.Parse(typeof(DayOfWeek), "Monday") == DayOfWeek.Monday);
		Check("DateTime and Stopwatch", () => DateTime.Now.Year >= 2024 && Stopwatch.Frequency > 0);
		Probe("basic checks");
		Check("thread start/join", () => {
			int shared = 0;
			var t = new Thread(() => { for (int i = 0; i < 100000; i++) Interlocked.Increment(ref shared); });
			t.Start();
			for (int i = 0; i < 100000; i++) Interlocked.Increment(ref shared);
			t.Join();
			return shared == 200000;
		});
		Check("lock and Monitor.Wait/Pulse", () => {
			object gate = new object();
			bool ready = false;
			var t = new Thread(() => { lock (gate) { ready = true; Monitor.Pulse(gate); } });
			lock (gate)
			{
				t.Start();
				while (!ready)
					if (!Monitor.Wait(gate, 5000))
						return false;
			}
			t.Join();
			return true;
		});
		Probe("thread start/join and Monitor");
		Check("thread pool", () => { var done = new ManualResetEvent(false); ThreadPool.QueueUserWorkItem(_ => done.Set()); return done.WaitOne(5000); });
		Probe("thread pool");
		Check("GC nursery collections", () => {
			// Small objects only: large arrays bypass the nursery.
			int before = GC.CollectionCount(0);
			for (int i = 0; i < 200000; i++) { var junk = new byte[100]; junk[0] = 1; }
			return GC.CollectionCount(0) > before;
		});
		Probe("nursery collections");
		Check("GC.Collect", () => {
			int before = GC.CollectionCount(GC.MaxGeneration);
			GC.Collect();
			return GC.CollectionCount(GC.MaxGeneration) > before;
		});
		Probe("GC.Collect");
		Check("finalizers", () => {
			for (int i = 0; i < 100; i++) new Finalizable();
			GC.Collect();
			GC.WaitForPendingFinalizers();
			return Finalizable.Finalized > 0;
		});
		Probe("finalizers");
		Check("file io", () => {
			string path = Path.Combine(dir, "io-test.txt");
			File.WriteAllText(path, "stardew");
			bool ok = File.ReadAllText(path) == "stardew" && File.Exists(path);
			File.Delete(path);
			return ok && !File.Exists(path);
		});
		Check("thread static", () => {
			threadStatic = 7;
			int seenByOther = -1;
			var t = new Thread(() => { seenByOther = threadStatic; threadStatic = 9; });
			t.Start();
			t.Join();
			return threadStatic == 7 && seenByOther == 0;
		});
		Check("generic thread static", () => { GenericHolder<char>.Value = new char[3][]; return GenericHolder<char>.Value.Length == 3; });
		Check("ArrayPoolEventSource.Log", () => typeof(object).Assembly.GetType("System.Buffers.ArrayPoolEventSource")
			.GetField("Log", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).GetValue(null) != null);
		Check("ArrayPool rent", () => System.Buffers.ArrayPool<char>.Shared.Rent(4096).Length >= 4096);
		Check("P/Invoke int return (GetReadDirRBufferSize)", () => {
			var sys = typeof(object).Assembly.GetType("Interop+Sys");
			var method = sys.GetMethod("GetReadDirRBufferSize", BindingFlags.NonPublic | BindingFlags.Static);
			int size = (int)method.Invoke(null, null);
			log.WriteLine("     GetReadDirRBufferSize() = {0}", size);
			return size >= 0 && size < 4096;
		});
		Check("directory listing", () => Directory.GetFiles(dir).Any(f => f.EndsWith("managed.txt")));
		Check("rooted device paths", () => Path.IsPathRooted(dir) && Path.GetFullPath(Path.Combine(dir, "a/../b.txt")) == Path.Combine(dir, "b.txt"));
		Check("file stream append", () => {
			string path = Path.Combine(dir, "stream-test.txt");
			using (var w = new StreamWriter(path)) w.Write("one");
			using (var w = new StreamWriter(path, true)) w.Write("two");
			bool ok = File.ReadAllText(path) == "onetwo" && new FileInfo(path).Length == 6;
			File.Delete(path);
			return ok;
		});
		Check("xml", () => {
			var doc = new XmlDocument();
			doc.LoadXml("<Farm name=\"Yaman\"><Crop days=\"4\" /></Farm>");
			return doc.DocumentElement.GetAttribute("name") == "Yaman" && doc.SelectSingleNode("/Farm/Crop/@days").Value == "4";
		});

		// Rough speed figures for comparing execution modes.
		var bench = Stopwatch.StartNew();
		int fib = Fib(24);
		long fibMs = bench.ElapsedMilliseconds;
		bench.Restart();
		double sum = 0;
		for (int i = 0; i < 2000000; i++)
			sum += i * 0.5;
		long loopMs = bench.ElapsedMilliseconds;
		log.WriteLine("bench: Fib(24)={0} in {1} ms, 2M float loop in {2} ms (sum {3})", fib, fibMs, loopMs, sum);
		log.WriteLine("GC: {0} KB total memory, collections gen0 {1}, gen1 {2}", GC.GetTotalMemory(false) / 1024, GC.CollectionCount(0), GC.CollectionCount(1));
		log.WriteLine("failures: {0}", failures);
		log.Close();
		return failures == 0 ? 42 : 1;
	}
}
