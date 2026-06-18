using System;
using System.Collections.Generic;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor;

public sealed class EditorSceneAssetFile
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".scene.json";

	public int Version { get; set; } = CurrentVersion;
	public string Name { get; set; } = string.Empty;
	public Guid GlobalCellId { get; set; }
	public List<SceneSpatialCellFileEntry> SpatialCells { get; set; } = [];

	public static string GlobalCellNodeKey => "cell:global";

	public static string GetSpatialCellNodeKey(Int2 coordinates) => $"cell:spatial:{coordinates.X}:{coordinates.Y}";

	public static string GetGlobalCellAssetName(string sceneName)
	{
		return $"{(string.IsNullOrWhiteSpace(sceneName) ? "Scene" : sceneName)} Global Cell";
	}

	public static string GetSpatialCellAssetName(string sceneName, Int2 coordinates)
	{
		var prefix = string.IsNullOrWhiteSpace(sceneName) ? "Scene" : sceneName;
		return $"{prefix} Cell {coordinates.X}, {coordinates.Y}";
	}

	public static EditorSceneAssetFile Load(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("Scene asset path cannot be null or empty.", nameof(path));
		}

		var json = File.ReadAllText(path);
		var file = JsonSerializer.Deserialize<EditorSceneAssetFile>(json, AssetJson.SerializerOptions)
			?? throw new InvalidOperationException($"Failed to deserialize scene asset '{path}'.");
		if (file.Version != CurrentVersion)
		{
			throw new InvalidOperationException($"Unsupported scene asset version {file.Version}. Expected {CurrentVersion}.");
		}

		file.SpatialCells ??= [];
		return file;
	}
}

public sealed class SceneSpatialCellFileEntry
{
	public int X { get; set; }
	public int Y { get; set; }
	public Guid CellId { get; set; }

	public Int2 ToCoordinates() => new(X, Y);
}
