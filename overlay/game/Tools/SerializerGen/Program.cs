using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Serialization;

// Writes StardewValley.SaveSerialization/Generated/XmlSerializers.cs: the serializers the game builds
// at run time (SaveGame.serializer and friends, SaveSerializer.GetSerializer types), generated ahead
// of time by .NET Framework's XmlSerializer code generator.
//
//   SerializerGen <game build folder> <output .cs>
//
// Building a serializer at run time reflects over every type it can reach and emits IL; on the Vita
// that took ~150 s for the save serializers. The generated code is plain C#: no reflection, no IL
// emission. It must be regenerated whenever a serialized type changes (Tools/SerializerGen/regenerate.ps1).
internal static class Program
{
	private static int Main(string[] args)
	{
		if (args.Length == 2 && args[0] == "--postprocess")
		{
			// Applies the post-processing below to an already generated file.
			string path = Path.GetFullPath(args[1]);
			List<string> lines = ConstructDirectly(CacheReaderIDs(File.ReadAllLines(path).ToList()));
			File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			Console.WriteLine($"post-processed {path}");
			return 0;
		}
		if (args.Length != 2)
		{
			Console.Error.WriteLine("usage: SerializerGen <game build folder> <output .cs>");
			Console.Error.WriteLine("       SerializerGen --postprocess <generated .cs>");
			return 2;
		}
		string gameDir = Path.GetFullPath(args[0]);
		string output = Path.GetFullPath(args[1]);
		AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
		{
			string name = new AssemblyName(e.Name).Name;
			foreach (string extension in new[] { ".dll", ".exe" })
			{
				string path = Path.Combine(gameDir, name + extension);
				if (File.Exists(path))
				{
					return Assembly.LoadFrom(path);
				}
			}
			return null;
		};
		Assembly game = Assembly.LoadFrom(Path.Combine(gameDir, "Stardew Valley.exe"));
		Type T(string name) => game.GetType(name, throwOnError: true);

		Type character = T("StardewValley.Character");
		Type location = T("StardewValley.GameLocation");
		Type item = T("StardewValley.Item");
		Type quest = T("StardewValley.Quests.Quest");
		Type terrainFeature = T("StardewValley.TerrainFeatures.TerrainFeature");
		Type descriptionElement = T("StardewValley.Quests.DescriptionElement");

		// The serializers SaveGame.cs and SaveSerializer.GetSerializer build. They share one importer, so
		// the generated read/write methods for the game's types exist once (separate importers repeated
		// them per serializer: 47 MB of source). Its extra types are SaveGame's, a superset of the other
		// constructors' (Farmer: Item; GameLocation: Character, Item, TerrainFeature; DescriptionElement:
		// Character, Item): every document those read or write is handled the same way.
		// SaveMigrator_1_6.LegacyDescriptionElement stays a run-time serializer: it shares
		// DescriptionElement's XML name, and only saves from before 1.6 need it.
		Type[] extraTypes = { character, location, item, quest, terrainFeature };
		var specs = new List<Type>
		{
			T("StardewValley.SaveGame"),
			T("StardewValley.Farmer"),
			location,
			descriptionElement,
			T("StardewValley.Options"),
			T("StardewValley.StartupPreferences"),
			// Requested through SaveSerializer.GetSerializer while a save loads (Netcode collections and
			// dictionaries serialize their values one by one); logged on the Vita as built at run time.
			item,
			T("StardewValley.Object"),
			T("StardewValley.FarmAnimal"),
			terrainFeature,
			T("StardewValley.Inventories.Inventory"),
			T("StardewValley.Friendship"),
			character,
			T("StardewValley.NPC"),
			quest,
			T("StardewValley.Buildings.Building"),
		};
		var covered = new HashSet<Type>(specs);
		foreach (Type type in DictionaryArgumentTypes(game).OrderBy(type => type.FullName, StringComparer.Ordinal))
		{
			// One generated assembly serves the types of one assembly. The others (Vector2, Point, arrays
			// of primitives) are small enough to build at run time.
			if (type.Assembly != game)
			{
				Console.WriteLine($"left to run time: {type.FullName}");
				continue;
			}
			if (covered.Add(type))
			{
				specs.Add(type);
			}
		}

		var types = new List<Type>();
		var mappings = new List<XmlMapping>();
		var importer = new XmlReflectionImporter();
		foreach (Type extraType in extraTypes)
		{
			importer.IncludeType(extraType);
		}
		foreach (Type type in specs)
		{
			try
			{
				mappings.Add(importer.ImportTypeMapping(type));
				types.Add(type);
				Console.WriteLine($"mapping {type.FullName}");
			}
			catch (Exception e)
			{
				Console.WriteLine($"skipped {type.FullName}: {e.GetBaseException().Message}");
			}
		}

		string temp = Path.Combine(Path.GetTempPath(), "SerializerGen-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(temp);
		var parameters = new CompilerParameters
		{
			GenerateInMemory = false,
			OutputAssembly = Path.Combine(temp, "StardewValley.XmlSerializers.dll"),
			TempFiles = new TempFileCollection(temp, keepFiles: true),
		};
		XmlSerializer.GenerateSerializer(types.ToArray(), mappings.ToArray(), parameters);
		string source = Directory.GetFiles(temp, "*.cs").Single();

		var text = new StringBuilder();
		text.AppendLine("// <auto-generated>");
		text.AppendLine("// Generated by Tools/SerializerGen from the game build (XmlSerializer.GenerateSerializer on");
		text.AppendLine("// .NET Framework). Do not edit; regenerate with Tools/SerializerGen/regenerate.ps1 whenever a");
		text.AppendLine("// serialized type changes.");
		text.AppendLine("// </auto-generated>");
		text.AppendLine("#pragma warning disable");
		foreach (string line in ConstructDirectly(CacheReaderIDs(File.ReadAllLines(source).ToList())))
		{
			// Assembly attributes belong to a standalone serializer assembly; this code is compiled into the game.
			if (line.TrimStart().StartsWith("[assembly:", StringComparison.Ordinal))
			{
				continue;
			}
			text.AppendLine(line);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(output));
		File.WriteAllText(output, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		try
		{
			Directory.Delete(temp, recursive: true);
		}
		catch (Exception)
		{
			// The generated assembly is still loaded in this process; the temp folder is left behind.
		}
		Console.WriteLine($"wrote {output} ({new FileInfo(output).Length / 1024} KB, {mappings.Count} serializers)");
		return 0;
	}

	/// <summary>
	/// Replaces the generated reader's reflection construction of default-constructible types
	/// (System.Activator.CreateInstance(typeof(T), flags, null, new object[0], null)) with a direct
	/// <c>new T()</c>. Same result for structs and public-constructor classes; avoids the reflection call
	/// per Vector2/Point/SaveablePair read, which showed up on the Vita load profile.
	/// </summary>
	private static List<string> ConstructDirectly(List<string> lines)
	{
		var create = new System.Text.RegularExpressions.Regex(@"\(([^()]+)\)System\.Activator\.CreateInstance\(typeof\(\1\), System\.Reflection\.BindingFlags\.Instance \| System\.Reflection\.BindingFlags\.Public \| System\.Reflection\.BindingFlags\.CreateInstance \| System\.Reflection\.BindingFlags\.NonPublic, null, new object\[0\], null\)");
		for (int i = 0; i < lines.Count; i++)
		{
			lines[i] = create.Replace(lines[i], "new $1()");
		}
		return lines;
	}

	/// <summary>
	/// Rewrites the reader's InitIDs, which adds every element and attribute name the serializers know
	/// (~1460) to the XmlReader's name table. It runs for each Deserialize call, and SerializableDictionary
	/// deserializes every key and value on its own: on the Vita that was ~40% of reading a save. The
	/// rewritten InitIDs adds the names once per name table and copies the atoms after that.
	/// </summary>
	private static List<string> CacheReaderIDs(List<string> lines)
	{
		var add = new System.Text.RegularExpressions.Regex("^(\\s*)(id\\d+_\\w+) = Reader\\.NameTable\\.Add\\((@\"(?:[^\"]|\"\")*\")\\);$");
		var result = new List<string>(lines.Count + 4000);
		for (int i = 0; i < lines.Count; i++)
		{
			string line = lines[i];
			result.Add(line);
			if (line.Trim() != "protected override void InitIDs() {")
			{
				continue;
			}
			var fields = new List<string>();
			var names = new List<string>();
			string indent = null;
			int j = i + 1;
			for (; j < lines.Count; j++)
			{
				var match = add.Match(lines[j]);
				if (!match.Success)
				{
					break;
				}
				indent = indent ?? match.Groups[1].Value;
				fields.Add(match.Groups[2].Value);
				names.Add(match.Groups[3].Value);
			}
			if (fields.Count == 0 || lines[j].Trim() != "}")
			{
				// Empty, already rewritten, or a shape this doesn't know: left as it is.
				continue;
			}
			string outer = line.Substring(0, line.Length - line.TrimStart().Length);
			result.Add(indent + "// SerializerGen: the names are added to a name table once, then the atoms are reused.");
			result.Add(indent + "System.Xml.XmlNameTable table = Reader.NameTable;");
			result.Add(indent + "InitIDsAtoms atoms = s_initIDsAtoms;");
			result.Add(indent + "if (atoms == null || atoms.Table != table) {");
			result.Add(indent + "    string[] added = new string[InitIDsNames.Length];");
			result.Add(indent + "    for (int i = 0; i < added.Length; i++) {");
			result.Add(indent + "        added[i] = table.Add(InitIDsNames[i]);");
			result.Add(indent + "    }");
			result.Add(indent + "    atoms = new InitIDsAtoms(table, added);");
			result.Add(indent + "    s_initIDsAtoms = atoms;");
			result.Add(indent + "}");
			result.Add(indent + "string[] ids = atoms.Ids;");
			for (int k = 0; k < fields.Count; k++)
			{
				result.Add($"{indent}{fields[k]} = ids[{k}];");
			}
			result.Add(lines[j]);
			result.Add("");
			result.Add(outer + "private sealed class InitIDsAtoms {");
			result.Add(outer + "    public readonly System.Xml.XmlNameTable Table;");
			result.Add(outer + "    public readonly string[] Ids;");
			result.Add(outer + "    public InitIDsAtoms(System.Xml.XmlNameTable table, string[] ids) { Table = table; Ids = ids; }");
			result.Add(outer + "}");
			result.Add("");
			result.Add(outer + "private static InitIDsAtoms s_initIDsAtoms;");
			result.Add("");
			result.Add(outer + "private static readonly string[] InitIDsNames = new string[] {");
			foreach (string name in names)
			{
				result.Add($"{indent}{name},");
			}
			result.Add(outer + "};");
			i = j;
		}
		return result;
	}

	/// <summary>Key and value types of every SerializableDictionary the game declares (SerializableDictionary's static constructor gets a serializer for each).</summary>
	private static IEnumerable<Type> DictionaryArgumentTypes(Assembly game)
	{
		Type dictionary = game.GetType("StardewValley.SerializableDictionary`2", throwOnError: true);
		var found = new HashSet<Type>();
		const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		void Visit(Type type)
		{
			if (type == null)
			{
				return;
			}
			if (type.IsArray)
			{
				Visit(type.GetElementType());
				return;
			}
			if (!type.IsGenericType)
			{
				return;
			}
			if (type.GetGenericTypeDefinition() == dictionary)
			{
				foreach (Type argument in type.GetGenericArguments())
				{
					if (!argument.IsGenericParameter && !argument.ContainsGenericParameters && NeedsMapping(argument))
					{
						found.Add(argument);
					}
				}
			}
			foreach (Type argument in type.GetGenericArguments())
			{
				Visit(argument);
			}
		}
		Type[] gameTypes;
		try
		{
			gameTypes = game.GetTypes();
		}
		catch (ReflectionTypeLoadException e)
		{
			gameTypes = e.Types.Where(type => type != null).ToArray();
		}
		foreach (Type type in gameTypes)
		{
			for (Type baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
			{
				Visit(baseType);
			}
			foreach (FieldInfo field in type.GetFields(all))
			{
				Visit(field.FieldType);
			}
			foreach (PropertyInfo property in type.GetProperties(all))
			{
				Visit(property.PropertyType);
			}
		}
		return found;
	}

	/// <summary>Primitive types and strings get a built-in mapping at run time; everything else is imported.</summary>
	private static bool NeedsMapping(Type type)
	{
		return !(type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) || type == typeof(byte[]) || type == typeof(TimeSpan));
	}
}
