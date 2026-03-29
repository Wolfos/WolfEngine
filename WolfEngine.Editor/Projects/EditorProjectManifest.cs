using System;
using System.IO;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor.Projects;

internal sealed class EditorProjectManifest
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public string GameplayProjectRelativePath { get; set; } = string.Empty;
}

internal static class EditorProjectManifestFile
{
	public const string FileName = "WolfEngineProject.json";

	public static string GetPath(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		return Path.Combine(projectRootPath, FileName);
	}

	public static EditorProjectManifest Load(string projectRootPath)
	{
		var absolutePath = GetPath(projectRootPath);
		var json = File.ReadAllText(absolutePath);
		var manifest = JsonSerializer.Deserialize<EditorProjectManifest>(json, AssetJson.SerializerOptions)
		               ?? throw new InvalidOperationException($"Failed to deserialize project manifest '{absolutePath}'.");
		if (manifest.Version != EditorProjectManifest.CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported project manifest version {manifest.Version}. Expected {EditorProjectManifest.CurrentVersion}.");
		}

		manifest.GameplayProjectRelativePath = ProjectPathUtility.NormalizeRelativePath(manifest.GameplayProjectRelativePath).Trim('/');
		return manifest;
	}

	public static void Save(string projectRootPath, EditorProjectManifest manifest)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		ArgumentNullException.ThrowIfNull(manifest);

		manifest.Version = EditorProjectManifest.CurrentVersion;
		manifest.GameplayProjectRelativePath = ProjectPathUtility.NormalizeRelativePath(manifest.GameplayProjectRelativePath).Trim('/');
		WriteJsonAtomically(GetPath(projectRootPath), manifest);
	}

	private static void WriteJsonAtomically<T>(string path, T value)
	{
		var directory = Path.GetDirectoryName(path);
		if (string.IsNullOrWhiteSpace(directory) == false)
		{
			Directory.CreateDirectory(directory);
		}

		var tempPath = path + ".tmp";
		var json = JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
		File.WriteAllText(tempPath, json);
		File.Move(tempPath, path, true);
	}
}
