using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

/// <summary>
/// The gameplay source files a build reads, as they were when they were scanned.
/// </summary>
public sealed class GameplaySourceSnapshot
{
	/// <summary>Input paths relative to the gameplay project directory, sorted, with forward slashes.</summary>
	public required IReadOnlyList<string> InputPaths { get; init; }

	/// <summary>The latest last-write time of any input, or <see cref="DateTime.MinValue"/> when there are none.</summary>
	public required DateTime NewestWriteTimeUtc { get; init; }

	/// <summary>Changes whenever an input is added, removed, renamed or written.</summary>
	public required string Fingerprint { get; init; }
}

/// <summary>
/// What the editor knew about the gameplay source when it last built or checked the gameplay assembly.
/// </summary>
public sealed class GameplayBuildStamp
{
	public string AssemblyPath { get; set; } = string.Empty;
	public DateTime AssemblyWriteTimeUtc { get; set; }
	public DateTime NewestInputWriteTimeUtc { get; set; }
	public List<string> InputPaths { get; set; } = [];
}

/// <summary>
/// Decides whether a built gameplay assembly is older than the gameplay source it was built from.
/// </summary>
/// <remarks>
/// Scenes deserialized with a stale assembly lose the components and fields that only the newer source has, and
/// saving writes that loss back to disk. The editor rebuilds before loading a scene when this reports stale, so the
/// check must be cheap when the assembly is fresh: it only lists and stats the input files.
/// <para>
/// Timestamps alone miss a deleted or renamed file, which leaves every remaining input older than the assembly. The
/// stamp written after each build records the input list to catch that. An assembly the editor did not build itself,
/// as from an IDE, has no matching stamp; it is then compared by timestamps only.
/// </para>
/// </remarks>
public static class GameplaySourceFreshness
{
	public const string StampFileName = "build-stamp.json";

	private static readonly HashSet<string> InputExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".cs",
		".razor",
		".cshtml",
		".css",
		".resx",
		".csproj",
		".props",
		".targets"
	};

	private static readonly string[] AncestorInputFileNames =
	[
		"Directory.Build.props",
		"Directory.Build.targets",
		"Directory.Packages.props",
		"global.json"
	];

	private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
	{
		"bin",
		"obj"
	};

	/// <summary>
	/// Whether a change at <paramref name="path"/>, a file or a directory, can change the gameplay build.
	/// </summary>
	public static bool CanAffectBuild(string gameplayProjectDirectory, string path)
	{
		var relativePath = Path.GetRelativePath(gameplayProjectDirectory, path);
		if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
		{
			return false;
		}

		var segments = relativePath.Split(
			[Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
			StringSplitOptions.RemoveEmptyEntries);
		for (var i = 0; i < segments.Length - 1; i++)
		{
			if (IsExcludedDirectoryName(segments[i]))
			{
				return false;
			}
		}

		// A path without an extension is taken for a directory: deleting or renaming one removes every input in it.
		var extension = Path.GetExtension(path);
		if (extension.Length == 0)
		{
			return segments.Length > 0 && IsExcludedDirectoryName(segments[^1]) == false;
		}

		return InputExtensions.Contains(extension);
	}

	public static GameplaySourceSnapshot Scan(string gameplayProjectPath, string projectRootPath)
	{
		var gameplayProjectDirectory = Path.GetDirectoryName(Path.GetFullPath(gameplayProjectPath))
		                               ?? throw new InvalidOperationException($"Gameplay project '{gameplayProjectPath}' has no parent directory.");
		var inputs = new List<(string RelativePath, DateTime WriteTimeUtc)>();
		CollectProjectInputs(gameplayProjectDirectory, gameplayProjectDirectory, inputs);
		CollectAncestorInputs(gameplayProjectDirectory, Path.GetFullPath(projectRootPath), inputs);

		inputs.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));

		var newestWriteTimeUtc = DateTime.MinValue;
		var fingerprint = new StringBuilder();
		var inputPaths = new List<string>(inputs.Count);
		foreach (var input in inputs)
		{
			inputPaths.Add(input.RelativePath);
			if (input.WriteTimeUtc > newestWriteTimeUtc)
			{
				newestWriteTimeUtc = input.WriteTimeUtc;
			}

			fingerprint.Append(input.RelativePath);
			fingerprint.Append('|');
			fingerprint.Append(input.WriteTimeUtc.Ticks);
			fingerprint.Append('\n');
		}

		return new()
		{
			InputPaths = inputPaths,
			NewestWriteTimeUtc = newestWriteTimeUtc,
			Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())))
		};
	}

	/// <summary>
	/// Whether the assembly at <paramref name="assemblyPath"/> was built from older source than
	/// <paramref name="source"/>.
	/// </summary>
	public static bool IsStale(GameplaySourceSnapshot source, string assemblyPath, GameplayBuildStamp? stamp)
	{
		ArgumentNullException.ThrowIfNull(source);

		if (File.Exists(assemblyPath) == false)
		{
			return true;
		}

		var assemblyWriteTimeUtc = File.GetLastWriteTimeUtc(assemblyPath);
		if (stamp is not null &&
		    string.Equals(stamp.AssemblyPath, Path.GetFullPath(assemblyPath), StringComparison.OrdinalIgnoreCase) &&
		    stamp.AssemblyWriteTimeUtc == assemblyWriteTimeUtc)
		{
			return source.NewestWriteTimeUtc > stamp.NewestInputWriteTimeUtc ||
			       source.InputPaths.SequenceEqual(stamp.InputPaths, StringComparer.Ordinal) == false;
		}

		return source.NewestWriteTimeUtc > assemblyWriteTimeUtc;
	}

	/// <summary>
	/// Records that the assembly at <paramref name="assemblyPath"/> is current for <paramref name="source"/>.
	/// </summary>
	/// <remarks>
	/// For a build, scan <paramref name="source"/> before the build starts: a file saved while it runs then still
	/// reads as newer than the stamp.
	/// </remarks>
	public static GameplayBuildStamp CreateStamp(GameplaySourceSnapshot source, string assemblyPath)
	{
		ArgumentNullException.ThrowIfNull(source);

		return new()
		{
			AssemblyPath = Path.GetFullPath(assemblyPath),
			AssemblyWriteTimeUtc = File.GetLastWriteTimeUtc(assemblyPath),
			NewestInputWriteTimeUtc = source.NewestWriteTimeUtc,
			InputPaths = source.InputPaths.ToList()
		};
	}

	public static string GetStampPath(string projectRootPath)
	{
		return Path.Combine(AssetPipelinePaths.GetLibraryPath(projectRootPath), "Gameplay", StampFileName);
	}

	public static GameplayBuildStamp? TryReadStamp(string projectRootPath)
	{
		var stampPath = GetStampPath(projectRootPath);
		if (File.Exists(stampPath) == false)
		{
			return null;
		}

		try
		{
			return JsonSerializer.Deserialize<GameplayBuildStamp>(File.ReadAllText(stampPath), AssetJson.SerializerOptions);
		}
		catch (Exception exception)
		{
			Console.WriteLine($"[GameplayReload] Ignoring unreadable build stamp '{stampPath}': {exception.Message}");
			return null;
		}
	}

	public static void WriteStamp(string projectRootPath, GameplayBuildStamp stamp)
	{
		ArgumentNullException.ThrowIfNull(stamp);

		var stampPath = GetStampPath(projectRootPath);
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
			File.WriteAllText(stampPath, JsonSerializer.Serialize(stamp, AssetJson.SerializerOptions));
		}
		catch (Exception exception)
		{
			// Without a stamp the next check compares timestamps only, which is still safe.
			Console.WriteLine($"[GameplayReload] Failed to write build stamp '{stampPath}': {exception.Message}");
		}
	}

	private static void CollectProjectInputs(
		string gameplayProjectDirectory,
		string directory,
		List<(string RelativePath, DateTime WriteTimeUtc)> inputs)
	{
		foreach (var filePath in Directory.EnumerateFiles(directory))
		{
			if (InputExtensions.Contains(Path.GetExtension(filePath)) == false)
			{
				continue;
			}

			inputs.Add((ToRelativeInputPath(gameplayProjectDirectory, filePath), File.GetLastWriteTimeUtc(filePath)));
		}

		foreach (var childDirectory in Directory.EnumerateDirectories(directory))
		{
			if (IsExcludedDirectoryName(Path.GetFileName(childDirectory)))
			{
				continue;
			}

			CollectProjectInputs(gameplayProjectDirectory, childDirectory, inputs);
		}
	}

	private static void CollectAncestorInputs(
		string gameplayProjectDirectory,
		string projectRootPath,
		List<(string RelativePath, DateTime WriteTimeUtc)> inputs)
	{
		var relativeToRoot = Path.GetRelativePath(projectRootPath, gameplayProjectDirectory);
		if (relativeToRoot.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot))
		{
			return;
		}

		var directory = Directory.GetParent(gameplayProjectDirectory);
		while (directory is not null)
		{
			foreach (var fileName in AncestorInputFileNames)
			{
				var filePath = Path.Combine(directory.FullName, fileName);
				if (File.Exists(filePath))
				{
					inputs.Add((ToRelativeInputPath(gameplayProjectDirectory, filePath), File.GetLastWriteTimeUtc(filePath)));
				}
			}

			if (string.Equals(
				    Path.TrimEndingDirectorySeparator(directory.FullName),
				    Path.TrimEndingDirectorySeparator(projectRootPath),
				    StringComparison.OrdinalIgnoreCase))
			{
				break;
			}

			directory = directory.Parent;
		}
	}

	private static bool IsExcludedDirectoryName(string name)
	{
		return ExcludedDirectoryNames.Contains(name) || name.StartsWith('.');
	}

	private static string ToRelativeInputPath(string gameplayProjectDirectory, string filePath)
	{
		return Path.GetRelativePath(gameplayProjectDirectory, filePath).Replace('\\', '/');
	}
}

public static class GameplayStaleBuildMessage
{
	/// <summary>
	/// The warning shown when a rebuild of changed gameplay source failed and an older build stays in use.
	/// </summary>
	public static string Create(string? buildOutput)
	{
		var message = "Gameplay build failed, so the editor is using a gameplay build older than the source. " +
		              "Scenes opened now leave out components and fields added since that build, and saving them " +
		              "removes those from the file. Fix the build and reload gameplay before saving.";
		if (string.IsNullOrWhiteSpace(buildOutput))
		{
			return message;
		}

		return message + Environment.NewLine + Environment.NewLine + buildOutput;
	}
}
