using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Editor;

public sealed class PrefabAssetFile
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".prefab.json";

	public int Version { get; set; } = CurrentVersion;
	public string Name { get; set; } = string.Empty;
	public Guid RootEntityId { get; set; }
	public List<SavedEntity> Entities { get; set; } = [];

	public static PrefabAssetFile Load(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Prefab asset path cannot be null or empty.", nameof(path));
		}

		var json = File.ReadAllText(path);
		var file = JsonSerializer.Deserialize<PrefabAssetFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize prefab asset '{path}'.");
		if (file.Version != CurrentVersion)
		{
			throw new InvalidOperationException($"Unsupported prefab asset version {file.Version}. Expected {CurrentVersion}.");
		}

		file.Entities ??= [];
		return file;
	}
}
