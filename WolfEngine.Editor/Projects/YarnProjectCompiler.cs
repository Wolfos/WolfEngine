using System.Security.Cryptography;
using System.Text;
using WolfEngine.Dialogue;
using Yarn.Compiler;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// Compiles a <c>.yarnproject</c> and every <c>.yarn</c> file it names into one <see cref="YarnProject"/> artifact.
/// Functions scripts call are type-checked against the definitions file the project names (a <c>.ysls.json</c>), the
/// same file the Yarn Spinner VS Code extension reads, so writers and the importer see the same errors.
/// </summary>
public static class YarnProjectCompiler
{
	/// <summary>Compiles the project at <paramref name="absoluteProjectPath"/> and writes its artifact.</summary>
	/// <exception cref="InvalidDataException">The project has compile errors; the message lists every one.</exception>
	public static YarnProjectSummary Compile(string projectRootPath, string absoluteProjectPath, string absoluteArtifactPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(absoluteProjectPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(absoluteArtifactPath);

		var project = Load(absoluteProjectPath);
		var sourceFiles = project.SourceFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray();
		if (sourceFiles.Length == 0)
			throw new InvalidDataException($"Yarn project '{Path.GetFileName(absoluteProjectPath)}' matches no .yarn files.");

		var result = Compiler.Compile(CompilationJob.CreateFromProject(project));
		var errors = result.Diagnostics.Where(IsError).ToArray();
		foreach (var warning in result.Diagnostics.Where(item => !IsError(item) && item.Severity == Diagnostic.DiagnosticSeverity.Warning))
		{
			Console.Error.WriteLine($"Yarn warning in {Describe(projectRootPath, warning)}");
		}

		if (errors.Length > 0 || result.Program is null)
		{
			throw new InvalidDataException(
				$"Yarn project '{Path.GetFileName(absoluteProjectPath)}' has {errors.Length} compile error(s):" + Environment.NewLine +
				string.Join(Environment.NewLine, errors.Select(error => "  " + Describe(projectRootPath, error))));
		}

		var lines = (result.StringTable ?? new Dictionary<string, StringInfo>())
			.ToDictionary(pair => pair.Key, pair => pair.Value.text ?? string.Empty, StringComparer.Ordinal);
		YarnProjectArtifact.Write(absoluteArtifactPath, result.Program, lines);
		return new YarnProjectSummary
		{
			NodeNames = result.Program.Nodes.Keys.Order(StringComparer.Ordinal).ToArray(),
			LineCount = lines.Count,
			SourceFiles = sourceFiles.Select(path => ToProjectRelative(projectRootPath, path)).ToArray()
		};
	}

	/// <summary>
	/// Hashes the project file together with every <c>.yarn</c> and definitions file it reads, so editing any one of them
	/// makes the project stale. A project that doesn't load hashes as its own file, and its import reports why.
	/// </summary>
	public static string ComputeInputsHash(string absoluteProjectPath)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		hash.AppendData(File.ReadAllBytes(absoluteProjectPath));
		try
		{
			var project = Load(absoluteProjectPath);
			var folder = Path.GetDirectoryName(Path.GetFullPath(absoluteProjectPath))!;
			foreach (var path in project.SourceFiles.Concat(project.DefinitionsFiles)
				         .Select(Path.GetFullPath)
				         .Distinct(StringComparer.OrdinalIgnoreCase)
				         .Order(StringComparer.OrdinalIgnoreCase))
			{
				hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(folder, path).Replace('\\', '/') + "\n"));
				if (File.Exists(path)) hash.AppendData(File.ReadAllBytes(path));
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException
			                                  or ArgumentException or InvalidOperationException)
		{
		}

		return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
	}

	/// <summary>
	/// Yarn Spinner only warns about a call to an undeclared function (YS0065), but the game would fail on it the moment
	/// the line runs. Every function a script calls must be declared in the project's definitions file.
	/// </summary>
	private const string UnknownFunctionCode = "YS0065";

	private static bool IsError(Diagnostic diagnostic) =>
		diagnostic.Severity == Diagnostic.DiagnosticSeverity.Error ||
		string.Equals(diagnostic.Code, UnknownFunctionCode, StringComparison.Ordinal);

	private static Project Load(string absoluteProjectPath)
	{
		var folder = Path.GetDirectoryName(Path.GetFullPath(absoluteProjectPath))!;
		return Project.LoadFromFile(absoluteProjectPath, folder);
	}

	private static string Describe(string projectRootPath, Diagnostic diagnostic)
	{
		var file = string.IsNullOrEmpty(diagnostic.FileName) ? "?" : ToProjectRelative(projectRootPath, diagnostic.FileName);
		return $"{file}:{diagnostic.Range.Start.Line + 1}: {diagnostic.Message}";
	}

	private static string ToProjectRelative(string projectRootPath, string path) =>
		Path.IsPathRooted(path) ? Path.GetRelativePath(projectRootPath, path).Replace('\\', '/') : path;
}
