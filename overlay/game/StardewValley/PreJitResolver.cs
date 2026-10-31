using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace StardewValley;

/// <summary>Resolves the closed generic names of the Mono jit log ("Dictionary`2&lt;Point, bool&gt;:TryInsert", "Enumerable:TryGetLast&lt;Point&gt;") for the pre-JIT list, and compiles them (VitaPlatform.CompileHotMethods).</summary>
/// <remarks>Never runs a static constructor: GetFunctionPointer compiles a method without running its type's .cctor, and .cctor entries are skipped. Types are looked up lazily by name (Assembly.GetType per loaded assembly, cached), so no table of every type is built on the 350 MB device.</remarks>
internal sealed class PreJitResolver
{
	private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

	private readonly Assembly[] _assemblies;

	private readonly Dictionary<string, Type> _typesByName = new Dictionary<string, Type>();

	private readonly Dictionary<string, MethodBase[]> _membersByType = new Dictionary<string, MethodBase[]>();

	private static readonly Dictionary<string, Type> Aliases = new Dictionary<string, Type>
	{
		{ "bool", typeof(bool) },
		{ "int", typeof(int) },
		{ "single", typeof(float) },
		{ "double", typeof(double) },
		{ "string", typeof(string) },
		{ "object", typeof(object) },
		{ "byte", typeof(byte) },
		{ "sbyte", typeof(sbyte) },
		{ "long", typeof(long) },
		{ "char", typeof(char) },
		{ "uint", typeof(uint) },
		{ "ulong", typeof(ulong) },
		{ "short", typeof(short) },
		{ "ushort", typeof(ushort) },
		{ "intptr", typeof(IntPtr) },
		{ "uintptr", typeof(UIntPtr) },
		{ "void", typeof(void) }
	};

	public PreJitResolver()
	{
		List<Assembly> list = new List<Assembly>();
		list.Add(typeof(object).Assembly);
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly != typeof(object).Assembly)
			{
				list.Add(assembly);
			}
		}
		_assemblies = list.ToArray();
	}

	/// <summary>Whether a list entry needs this resolver: a closed generic type ("`" in the type) or a generic method ("Name&lt;args&gt;").</summary>
	public static bool IsGenericEntry(string typeText, string methodText)
	{
		if (typeText.StartsWith("(wrapper", StringComparison.Ordinal))
		{
			return false;
		}
		if (typeText.IndexOf('`') >= 0)
		{
			return true;
		}
		return GenericMethodStart(methodText) > 0;
	}

	/// <summary>Index of the '&lt;' of a generic method name "Name&lt;args&gt;", or -1 (explicit interface implementations have a '.' before their '&lt;').</summary>
	private static int GenericMethodStart(string methodText)
	{
		int lt = methodText.IndexOf('<');
		if (lt > 0 && methodText[methodText.Length - 1] == '>' && methodText.IndexOf('.') < lt)
		{
			return lt;
		}
		return -1;
	}

	/// <summary>Compiles the method(s) of one entry; returns how many were compiled (0 = unresolved). Never throws.</summary>
	public int Compile(string typeText, string methodText, out bool resolved)
	{
		resolved = false;
		int compiled = 0;
		try
		{
			string methodName = methodText;
			Type[] methodArgs = null;
			int lt = GenericMethodStart(methodText);
			if (lt > 0)
			{
				methodName = methodText.Substring(0, lt);
				int pos = lt + 1;
				List<Type> args = ParseArgs(methodText, ref pos);
				methodArgs = args.ToArray();
			}
			if (methodName == ".cctor")
			{
				return 0;
			}
			if (!_membersByType.TryGetValue(typeText, out MethodBase[] candidates))
			{
				candidates = null;
				try
				{
					int pos = 0;
					Type type = ParseType(typeText, ref pos);
					if (type != null && !type.ContainsGenericParameters && !type.IsInterface)
					{
						List<MethodBase> found = new List<MethodBase>();
						found.AddRange(type.GetMethods(Members));
						found.AddRange(type.GetConstructors(Members));
						candidates = found.ToArray();
					}
				}
				catch (Exception)
				{
					candidates = null;
				}
				_membersByType[typeText] = candidates;
			}
			if (candidates == null)
			{
				return 0;
			}
			foreach (MethodBase candidate in candidates)
			{
				if (candidate.Name != methodName || candidate.IsAbstract || candidate.IsStatic && candidate.IsConstructor)
				{
					continue;
				}
				try
				{
					MethodBase method = candidate;
					if (methodArgs != null)
					{
						MethodInfo info = candidate as MethodInfo;
						if (info == null || !info.IsGenericMethodDefinition || info.GetGenericArguments().Length != methodArgs.Length)
						{
							continue;
						}
						method = info.MakeGenericMethod(methodArgs);
					}
					else if (candidate.ContainsGenericParameters)
					{
						continue;
					}
					resolved = true;
					// GetFunctionPointer has the method compiled to give its address (PrepareMethod is empty on Mono).
					method.MethodHandle.GetFunctionPointer();
					compiled++;
				}
				catch (Exception)
				{
				}
			}
		}
		catch (Exception)
		{
		}
		return compiled;
	}

	/// <summary>Parses "Ns.Outer`2/Inner/Deepest&lt;arg, arg&gt;" with optional "[]" suffixes; the argument list belongs to the whole chain (Mono prints it once, at the end).</summary>
	private Type ParseType(string s, ref int p)
	{
		StringBuilder name = new StringBuilder();
		List<Type> typeArgs = null;
		while (true)
		{
			if (p < s.Length && s[p] == '<')
			{
				// compiler-generated name such as <>c__DisplayClass1_0
				int depth = 0;
				do
				{
					char c = s[p];
					if (c == '<')
					{
						depth++;
					}
					else if (c == '>')
					{
						depth--;
					}
					name.Append(c);
					p++;
				}
				while (depth > 0 && p < s.Length);
			}
			while (p < s.Length && s[p] != '<' && s[p] != '>' && s[p] != ',' && s[p] != '/' && s[p] != '[' && s[p] != ']')
			{
				name.Append(s[p]);
				p++;
			}
			if (p < s.Length && s[p] == '/')
			{
				name.Append('+');
				p++;
				continue;
			}
			if (p < s.Length && s[p] == '<')
			{
				p++;
				List<Type> args = ParseArgs(s, ref p);
				if (typeArgs == null)
				{
					typeArgs = args;
				}
				else
				{
					typeArgs.AddRange(args);
				}
				if (p < s.Length && s[p] == '/')
				{
					name.Append('+');
					p++;
					continue;
				}
			}
			break;
		}
		string fullName = name.ToString();
		Type type;
		if (typeArgs == null && Aliases.TryGetValue(fullName, out type))
		{
		}
		else
		{
			type = FindType(fullName);
			if (type == null)
			{
				throw new TypeLoadException(fullName);
			}
			if (typeArgs != null)
			{
				if (!type.IsGenericTypeDefinition || type.GetGenericArguments().Length != typeArgs.Count)
				{
					throw new TypeLoadException(fullName);
				}
				type = type.MakeGenericType(typeArgs.ToArray());
			}
		}
		while (p < s.Length && s[p] == '[')
		{
			int rank = 1;
			p++;
			while (p < s.Length && s[p] == ',')
			{
				rank++;
				p++;
			}
			if (p >= s.Length || s[p] != ']')
			{
				throw new FormatException(s);
			}
			p++;
			type = rank == 1 ? type.MakeArrayType() : type.MakeArrayType(rank);
		}
		return type;
	}

	/// <summary>Parses "type, type, ...&gt;" (positioned after the opening "&lt;") up to and including the closing "&gt;".</summary>
	private List<Type> ParseArgs(string s, ref int p)
	{
		List<Type> args = new List<Type>(4);
		while (true)
		{
			args.Add(ParseType(s, ref p));
			if (p < s.Length && s[p] == ',')
			{
				p++;
				while (p < s.Length && s[p] == ' ')
				{
					p++;
				}
				continue;
			}
			if (p < s.Length && s[p] == '>')
			{
				p++;
				return args;
			}
			throw new FormatException(s);
		}
	}

	private Type FindType(string name)
	{
		if (_typesByName.TryGetValue(name, out Type type))
		{
			return type;
		}
		foreach (Assembly assembly in _assemblies)
		{
			try
			{
				type = assembly.GetType(name, throwOnError: false);
			}
			catch (Exception)
			{
				type = null;
			}
			if (type != null)
			{
				break;
			}
		}
		_typesByName[name] = type;
		return type;
	}
}
