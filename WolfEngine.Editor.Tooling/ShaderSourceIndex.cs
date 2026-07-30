// Import-graph tracking is a development hot-reload concern: it exists so a source edit only invalidates
// the programs that actually compile that source.

using System.Security.Cryptography;
using System.Text;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// A snapshot of the engine shader tree: one content hash per source file plus the import graph that links
/// them, so a program's fingerprint covers only its own transitive sources instead of the whole tree.
/// A file whose dependency directives cannot be resolved is opaque, and every program reaching it falls
/// back to the whole-tree fingerprint; over-approximating costs a recompile, under-approximating serves
/// stale bytecode.
/// </summary>
public sealed class ShaderSourceIndex
{
	private static readonly string[] DependencyDirectives = ["import", "__import", "__include", "#include", "module", "implementing"];

	private readonly Dictionary<string, SourceFile> _files;
	private readonly string _treeFingerprint;
	private readonly Dictionary<string, string> _fingerprintsBySource = new(StringComparer.OrdinalIgnoreCase);

	private ShaderSourceIndex(Dictionary<string, SourceFile> files, string treeFingerprint)
	{
		_files = files;
		_treeFingerprint = treeFingerprint;
	}

	public static ShaderSourceIndex Build(string shaderSourceRoot)
	{
		var root = Path.GetFullPath(shaderSourceRoot);
		Dictionary<string, SourceFile> files = new(StringComparer.OrdinalIgnoreCase);
		foreach (var path in Directory.EnumerateFiles(root, "*.slang", SearchOption.AllDirectories))
		{
			var relativePath = NormalizePath(Path.GetRelativePath(root, path));
			var content = File.ReadAllBytes(path);
			var file = new SourceFile(relativePath, Convert.ToHexString(SHA256.HashData(content)));
			file.RawImports.AddRange(ParseDependencyDirectives(Encoding.UTF8.GetString(content), out var opaque));
			file.IsOpaque = opaque;
			files[relativePath] = file;
		}

		foreach (var file in files.Values) ResolveImports(root, file, files);
		return new ShaderSourceIndex(files, HashFiles(files, new SortedSet<string>(files.Keys, StringComparer.Ordinal)));
	}

	/// <summary>Returns a fingerprint covering the given source and everything it transitively imports.</summary>
	public string GetFingerprint(string relativeSourcePath)
	{
		var entryPath = NormalizePath(relativeSourcePath);
		if (_fingerprintsBySource.TryGetValue(entryPath, out var cached)) return cached;
		SortedSet<string> closure = new(StringComparer.Ordinal);
		var fingerprint = CollectClosure(entryPath, closure)
			? _treeFingerprint
			: HashFiles(_files, closure);
		_fingerprintsBySource[entryPath] = fingerprint;
		return fingerprint;
	}

	/// <summary>Adds the file and its imports to <paramref name="closure"/>; returns true when the closure is opaque.</summary>
	private bool CollectClosure(string relativePath, SortedSet<string> closure)
	{
		if (_files.TryGetValue(relativePath, out var file) == false) return true;
		if (closure.Add(file.RelativePath) == false) return false;
		var opaque = file.IsOpaque;
		foreach (var import in file.Imports) opaque |= CollectClosure(import, closure);
		return opaque;
	}

	private static void ResolveImports(string root, SourceFile file, Dictionary<string, SourceFile> files)
	{
		var importerDirectory = Path.GetDirectoryName(Path.Combine(root, file.RelativePath))!;
		foreach (var rawImport in file.RawImports)
		{
			var resolved = ResolveImport(root, importerDirectory, rawImport, files);
			if (resolved is null) file.IsOpaque = true;
			else file.Imports.Add(resolved);
		}
	}

	private static string? ResolveImport(string root, string importerDirectory, string importPath, Dictionary<string, SourceFile> files)
	{
		string candidate;
		try
		{
			candidate = Path.GetFullPath(Path.Combine(importerDirectory, importPath));
		}
		catch (ArgumentException)
		{
			return null;
		}

		if (candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == false) return null;
		var relativePath = NormalizePath(Path.GetRelativePath(root, candidate));
		if (files.TryGetValue(relativePath, out var file)) return file.RelativePath;
		return files.TryGetValue(relativePath + ".slang", out file) ? file.RelativePath : null;
	}

	private static string HashFiles(Dictionary<string, SourceFile> files, IEnumerable<string> relativePaths)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var relativePath in relativePaths)
		{
			hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
			hash.AppendData(Encoding.UTF8.GetBytes(files.TryGetValue(relativePath, out var file) ? $"\0{file.ContentHash}\n" : "\0\n"));
		}

		return Convert.ToHexString(hash.GetHashAndReset());
	}

	/// <summary>
	/// Collects the quoted import targets of a Slang source. Sets <paramref name="opaque"/> when the file
	/// carries a dependency directive this parser cannot turn into a file path.
	/// </summary>
	private static List<string> ParseDependencyDirectives(string text, out bool opaque)
	{
		opaque = false;
		List<string> imports = [];
		foreach (var rawLine in text.Split('\n'))
		{
			var line = rawLine.AsSpan().Trim();
			if (StartsWithDirective(line, out var directive) == false) continue;
			if (directive == "import" && TryParseQuotedTarget(line["import".Length..], out var importPath))
				imports.Add(importPath);
			else
				opaque = true;
		}

		return imports;
	}

	private static bool StartsWithDirective(ReadOnlySpan<char> line, out string directive)
	{
		foreach (var candidate in DependencyDirectives)
		{
			if (line.StartsWith(candidate, StringComparison.Ordinal) == false) continue;
			if (line.Length > candidate.Length && IsIdentifierChar(line[candidate.Length])) continue;
			directive = candidate;
			return true;
		}

		directive = string.Empty;
		return false;
	}

	private static bool TryParseQuotedTarget(ReadOnlySpan<char> remainder, out string target)
	{
		target = string.Empty;
		var afterKeyword = remainder.TrimStart();
		if (afterKeyword.Length == remainder.Length || afterKeyword.StartsWith("\"") == false) return false;
		var closingQuote = afterKeyword[1..].IndexOf('"');
		if (closingQuote <= 0) return false;
		if (afterKeyword[(closingQuote + 2)..].TrimStart().StartsWith(";") == false) return false;
		target = afterKeyword[1..(closingQuote + 1)].ToString();
		return true;
	}

	private static bool IsIdentifierChar(char value) => char.IsLetterOrDigit(value) || value == '_';

	private static string NormalizePath(string path) => path.Replace('\\', '/');

	private sealed class SourceFile(string relativePath, string contentHash)
	{
		public string RelativePath { get; } = relativePath;
		public string ContentHash { get; } = contentHash;
		public List<string> RawImports { get; } = [];
		public List<string> Imports { get; } = [];
		public bool IsOpaque { get; set; }
	}
}
