using System.Text.Json;
using System.Text.RegularExpressions;
using Wolfie.IAE.Projects;

namespace Wolfie.IAE.ManagedAssets;

public sealed partial class ManagedAssetService
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public IReadOnlyList<WolfieTemplate> GetTemplates(string projectFile)
	{
		var root = Path.Combine(GetProjectRoot(projectFile), "Templates");
		return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Where(path => !IsTemporaryOrMetadata(path))
			.Select(path => new WolfieTemplate(Path.GetFileNameWithoutExtension(path),
				"Templates/" + Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')))
			.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(template => template.RelativePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	public ManagedAssetRecord CreateFromTemplate(string projectFile, string destinationFolder,
		string templateRelativePath, string assetName)
	{
		var folder = NormalizeAssetFolder(destinationFolder);
		var validatedName = ValidateAssetName(assetName);
		var projectRoot = GetProjectRoot(projectFile);
		var template = ResolveTemplate(projectRoot, templateRelativePath);
		if (!File.Exists(template)) throw new FileNotFoundException("The selected template does not exist.", template);
		var extension = Path.GetExtension(template);
		var relativeSource = folder + "/" + validatedName + extension;
		var source = ResolveWithinAssets(projectRoot, relativeSource);
		var metadata = GetMetadataPath(source);
		if (File.Exists(source) || File.Exists(metadata))
			throw new InvalidOperationException($"An asset named '{validatedName + extension}' already exists.");

		var record = new ManagedAssetRecord
		{
			SourceId = Guid.NewGuid(),
			ImporterId = ImporterFor(extension),
			SourcePath = relativeSource,
			Outputs = string.Equals(extension, ".blend", StringComparison.OrdinalIgnoreCase)
				? [new ManagedOutputRecord { Path = Path.ChangeExtension(relativeSource, ".fbx").Replace('\\', '/') }]
				: []
		};
		var directory = Path.GetDirectoryName(source)!;
		Directory.CreateDirectory(directory);
		var sourceTemp = source + ".wolfie-" + Guid.NewGuid().ToString("N") + ".tmp";
		var metadataTemp = metadata + ".wolfie-" + Guid.NewGuid().ToString("N") + ".tmp";
		var sourceCommitted = false;
		try
		{
			File.Copy(template, sourceTemp, false);
			File.WriteAllText(metadataTemp, JsonSerializer.Serialize(record, JsonOptions));
			File.Move(sourceTemp, source, false);
			sourceCommitted = true;
			File.Move(metadataTemp, metadata, false);
			return record;
		}
		catch
		{
			if (sourceCommitted && File.Exists(source)) File.Delete(source);
			throw;
		}
		finally
		{
			if (File.Exists(sourceTemp)) File.Delete(sourceTemp);
			if (File.Exists(metadataTemp)) File.Delete(metadataTemp);
			RemoveEmptyParents(directory, Path.Combine(projectRoot, "Assets"));
		}
	}

	public ManagedAssetRecord ManageTexture(WolfieProject project, string projectFile, string unityRelativePath)
	{
		var relativePath = NormalizeAssetPath(unityRelativePath);
		var unitySource = ResolveWithinAssets(project.UnityProjectPath, relativePath);
		if (!File.Exists(unitySource)) throw new FileNotFoundException("The Unity texture does not exist.", unitySource);
		var wolfieRoot = GetProjectRoot(projectFile);
		var wolfieSource = ResolveWithinAssets(wolfieRoot, relativePath);
		var metadataPath = GetMetadataPath(wolfieSource);

		if (File.Exists(metadataPath)) return Load(metadataPath);
		Directory.CreateDirectory(Path.GetDirectoryName(wolfieSource)!);
		CopyAtomically(unitySource, wolfieSource);
		var record = new ManagedAssetRecord
		{
			SourceId = Guid.NewGuid(),
			SourcePath = relativePath,
			Outputs = [new ManagedOutputRecord { Path = relativePath, UnityGuid = ReadUnityGuid(unitySource) }]
		};
		SaveAtomically(metadataPath, record);
		return record;
	}

	public void Unmanage(string projectFile, string relativeSourcePath)
	{
		var source = ResolveWithinAssets(GetProjectRoot(projectFile), NormalizeAssetPath(relativeSourcePath));
		var metadata = GetMetadataPath(source);
		if (!File.Exists(metadata)) throw new InvalidOperationException("This asset is not managed by Wolfie.");
		File.Delete(metadata);
		if (File.Exists(source)) File.Delete(source);
		RemoveEmptyParents(Path.GetDirectoryName(source)!, Path.Combine(GetProjectRoot(projectFile), "Assets"));
	}

	public IReadOnlyDictionary<string, ManagedAssetRecord> LoadAll(string projectFile)
	{
		var assetsRoot = Path.Combine(GetProjectRoot(projectFile), "Assets");
		var records = new Dictionary<string, ManagedAssetRecord>(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(assetsRoot)) return records;
		foreach (var metadata in Directory.EnumerateFiles(assetsRoot, "*.meta", SearchOption.AllDirectories))
		{
			var record = Load(metadata);
			if (!string.IsNullOrWhiteSpace(record.SourcePath)) records[NormalizeAssetPath(record.SourcePath)] = record;
		}
		return records;
	}

	public void PublishOutput(WolfieProject project, ManagedAssetRecord asset, string outputPath, Stream content)
	{
		var normalized = NormalizeAssetPath(outputPath);
		if (!asset.Outputs.Any(output => string.Equals(NormalizeAssetPath(output.Path), normalized, StringComparison.OrdinalIgnoreCase)))
			throw new InvalidOperationException("Wolfie cannot modify an unregistered Unity output.");
		var destination = ResolveWithinAssets(project.UnityProjectPath, normalized);
		Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
		var temporary = destination + ".wolfie-" + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) content.CopyTo(file);
			File.Move(temporary, destination, true);
		}
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	public ManagedAssetRecord Get(string projectFile, string relativeSourcePath)
	{
		var normalized = NormalizeAssetPath(relativeSourcePath);
		return LoadAll(projectFile).TryGetValue(normalized, out var asset)
			? asset
			: throw new InvalidOperationException("The source file is not a managed Wolfie asset.");
	}

	public void RefreshUnityGuids(WolfieProject project, string projectFile, ManagedAssetRecord asset)
	{
		foreach (var output in asset.Outputs)
		{
			var path = ResolveWithinAssets(project.UnityProjectPath, NormalizeAssetPath(output.Path));
			output.UnityGuid = ReadUnityGuid(path) ?? output.UnityGuid;
		}
		var source = ResolveWithinAssets(GetProjectRoot(projectFile), NormalizeAssetPath(asset.SourcePath));
		SaveAtomically(GetMetadataPath(source), asset);
	}

	public static string? ReadUnityGuid(string unityAssetPath)
	{
		var metaPath = unityAssetPath + ".meta";
		if (!File.Exists(metaPath)) return null;
		foreach (var line in File.ReadLines(metaPath))
		{
			var match = UnityGuidLine().Match(line);
			if (match.Success) return match.Groups[1].Value;
		}
		return null;
	}

	public static string GetMetadataPath(string sourcePath) => sourcePath + ".meta";
	private static string GetProjectRoot(string projectFile) => Path.GetDirectoryName(WolfiePath.NormalizeAbsolute(projectFile))!;

	private static ManagedAssetRecord Load(string path)
	{
		var record = JsonSerializer.Deserialize<ManagedAssetRecord>(File.ReadAllText(path), JsonOptions)
		             ?? throw new InvalidDataException($"Managed asset record is invalid: {path}");
		if (record.Version != ManagedAssetRecord.CurrentVersion || record.SourceId == Guid.Empty)
			throw new InvalidDataException($"Managed asset record has an invalid version or identity: {path}");
		return record;
	}

	private static void SaveAtomically(string path, ManagedAssetRecord record)
	{
		var temporary = path + ".tmp";
		try { File.WriteAllText(temporary, JsonSerializer.Serialize(record, JsonOptions)); File.Move(temporary, path, true); }
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	private static void CopyAtomically(string source, string destination)
	{
		var temporary = destination + ".tmp";
		try { File.Copy(source, temporary, true); File.Move(temporary, destination, true); }
		finally { if (File.Exists(temporary)) File.Delete(temporary); }
	}

	private static string NormalizeAssetPath(string path)
	{
		var normalized = path.Replace('\\', '/').TrimStart('/');
		if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || normalized.Contains("../", StringComparison.Ordinal))
			throw new ArgumentException("Asset paths must be beneath Assets.", nameof(path));
		return "Assets/" + normalized[7..];
	}

	private static string NormalizeAssetFolder(string path)
	{
		var normalized = path.Replace('\\', '/').TrimEnd('/');
		if (normalized != "Assets" && !normalized.StartsWith("Assets/", StringComparison.Ordinal))
			throw new ArgumentException("New managed assets must be created beneath Assets.", nameof(path));
		if (normalized.Split('/').Any(part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
			throw new ArgumentException("The destination folder path is invalid.", nameof(path));
		return normalized;
	}

	private static string ValidateAssetName(string name)
	{
		var trimmed = name.Trim();
		if (trimmed.Length == 0) throw new ArgumentException("Enter an asset name.", nameof(name));
		if (trimmed is "." or ".." || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
		    trimmed.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '/', '\\']) >= 0 ||
		    trimmed.Any(char.IsControl) || trimmed.EndsWith('.') || Path.HasExtension(trimmed))
			throw new ArgumentException("Asset names cannot contain a path, extension, or invalid filename characters.", nameof(name));
		return trimmed;
	}

	private static string ResolveTemplate(string projectRoot, string relativePath)
	{
		var normalized = relativePath.Replace('\\', '/').TrimStart('/');
		if (!normalized.StartsWith("Templates/", StringComparison.Ordinal) || normalized.Contains("../", StringComparison.Ordinal))
			throw new ArgumentException("The template path is invalid.", nameof(relativePath));
		var root = Path.Combine(projectRoot, "Templates");
		var result = WolfiePath.NormalizeAbsolute(Path.Combine(projectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
		if (!WolfiePath.IsWithin(result, root)) throw new ArgumentException("The template path escapes Templates.");
		return result;
	}

	private static string ImporterFor(string extension) => extension.ToLowerInvariant() switch
	{
		".png" or ".jpg" or ".jpeg" or ".tga" or ".tif" or ".tiff" or ".exr" or ".hdr" => "texture",
		".blend" => "blender",
		".spp" => "substance-painter",
		_ => "file"
	};

	private static bool IsTemporaryOrMetadata(string path) =>
		path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
		path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

	private static string ResolveWithinAssets(string root, string relativePath)
	{
		var result = WolfiePath.NormalizeAbsolute(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
		if (!WolfiePath.IsWithin(result, Path.Combine(root, "Assets"))) throw new ArgumentException("Asset path escapes Assets.");
		return result;
	}

	private static void RemoveEmptyParents(string directory, string assetsRoot)
	{
		while (!string.Equals(directory, assetsRoot, StringComparison.OrdinalIgnoreCase) &&
		       WolfiePath.IsWithin(directory, assetsRoot) && !Directory.EnumerateFileSystemEntries(directory).Any())
		{
			Directory.Delete(directory);
			directory = Path.GetDirectoryName(directory)!;
		}
	}

	[GeneratedRegex(@"^\s*guid:\s*([0-9a-fA-F]{32})\s*$")]
	private static partial Regex UnityGuidLine();
}

public sealed record WolfieTemplate(string Name, string RelativePath);
