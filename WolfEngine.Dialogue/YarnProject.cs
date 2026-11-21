using WolfEngine.AssetPipeline;

namespace WolfEngine.Dialogue;

/// <summary>What the editor shows about an imported Yarn project without loading its program.</summary>
public sealed class YarnProjectSummary
{
	/// <summary>Every node a conversation can start from, sorted.</summary>
	public string[] NodeNames { get; set; } = [];

	/// <summary>How many lines and options the project's string table holds.</summary>
	public int LineCount { get; set; }

	/// <summary>The project-relative <c>.yarn</c> files that were compiled together.</summary>
	public string[] SourceFiles { get; set; } = [];
}

public interface IYarnProjectRuntimeResolver : IRuntimeAssetResolver;

/// <summary>
/// A compiled Yarn Spinner project: every <c>.yarn</c> file a <c>.yarnproject</c> names, compiled together at import so
/// nodes can jump between files and share variable declarations. Games ship this compiled form only, never the compiler.
/// </summary>
[RuntimeAsset(AssetType.YarnProject, typeof(YarnProjectSummary), typeof(IYarnProjectRuntimeResolver))]
public sealed class YarnProject
{
	private readonly Dictionary<string, string> _lines;

	public YarnProject(string name, Yarn.Program program, IReadOnlyDictionary<string, string> lines)
	{
		ArgumentNullException.ThrowIfNull(name);
		ArgumentNullException.ThrowIfNull(program);
		ArgumentNullException.ThrowIfNull(lines);

		Name = name;
		Program = program;
		_lines = new Dictionary<string, string>(lines, StringComparer.Ordinal);
	}

	public string Name { get; }

	/// <summary>The compiled program the dialogue VM runs.</summary>
	public Yarn.Program Program { get; }

	/// <summary>The project's line text by line ID, in the base language. Text still holds its markup.</summary>
	public IReadOnlyDictionary<string, string> Lines => _lines;

	public IEnumerable<string> NodeNames => Program.Nodes.Keys;

	public bool HasNode(string nodeName) => nodeName is not null && Program.Nodes.ContainsKey(nodeName);

	/// <summary>The raw text of line <paramref name="lineId"/>, or null when the string table has no such line.</summary>
	public string? GetLineText(string lineId) => _lines.GetValueOrDefault(lineId);
}

/// <summary>
/// What the editor shows about one <c>.yarn</c> file. The file isn't compiled on its own: its <c>.yarnproject</c> compiles
/// it with the rest, so this lists only the node titles it declares.
/// </summary>
public sealed class YarnScriptSummary
{
	public string[] NodeNames { get; set; } = [];

	/// <summary>Reads the <c>title:</c> header of every node in <paramref name="source"/>, in file order.</summary>
	public static YarnScriptSummary Parse(string source)
	{
		ArgumentNullException.ThrowIfNull(source);
		var names = new List<string>();
		var inHeader = true;
		foreach (var rawLine in source.Split('\n'))
		{
			var line = rawLine.Trim();
			if (line == "---") inHeader = false;
			else if (line == "===") inHeader = true;
			else if (inHeader && line.StartsWith("title:", StringComparison.Ordinal))
			{
				var name = line["title:".Length..].Trim();
				if (name.Length > 0) names.Add(name);
			}
		}

		return new YarnScriptSummary { NodeNames = names.ToArray() };
	}
}

public static class YarnProjectConstants
{
	/// <summary>The extension of the script files a project compiles.</summary>
	public const string ScriptExtension = ".yarn";

	public static bool IsScriptSource(string path) =>
		path.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase);

	/// <summary>The extension of the project file the importer compiles from.</summary>
	public const string SourceExtension = ".yarnproject";

	public const string RuntimeArtifactKind = "RuntimeYarnProject";
	public const string RuntimeArtifactTarget = "generic";
	public const string RuntimeArtifactFileName = "dialogue.wolfyarn";

	public static bool IsSupportedSource(string path) =>
		path.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase);
}
