using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using WolfEngine.Mathematics;

namespace WolfEngine.Editor;

public sealed class Cell
{
	public const int CurrentVersion = 1;
	public const string FileExtension = ".cell.json";

	public int Version { get; set; } = CurrentVersion;
	public string RelativePath { get; set; } = string.Empty;
	public List<SavedEntity> Entities { get; set; } = [];
}

public sealed class SavedEntity
{
	public Guid EntityId { get; set; }
	public Guid? ParentEntityId { get; set; }
	public bool HasName { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool Enabled { get; set; } = true;
	public string Icon { get; set; } = string.Empty;
	public Matrix4x4? LocalTransform { get; set; }
	public List<SavedPrefabLink> PrefabSourcePath { get; set; } = [];
	public SavedPrefabOverrides PrefabOverrides { get; set; } = new();
	public List<SavedComponent> Components { get; set; } = [];
}

public sealed class SavedComponent
{
	public string Type { get; set; } = string.Empty;
	public string TypeId { get; set; } = string.Empty;
	public JsonElement Data { get; set; }
}

public sealed class SavedPrefabLink
{
	public Guid PrefabAssetId { get; set; }
	public Guid PrefabEntityId { get; set; }
}

public sealed class SavedPrefabOverrides
{
	public bool Name { get; set; }
	public bool Enabled { get; set; }
	public bool LocalTransform { get; set; }
	public List<string> ComponentTypeIds { get; set; } = [];

	public bool HasComponentOverride(string componentTypeId)
	{
		for (var i = 0; i < ComponentTypeIds.Count; i++)
		{
			if (string.Equals(ComponentTypeIds[i], componentTypeId, StringComparison.Ordinal))
			{
				return true;
			}
		}

		return false;
	}
}

public readonly struct SceneCellKey : IEquatable<SceneCellKey>
{
	public SceneCellKey(bool isGlobal, Int2 coordinates)
	{
		IsGlobal = isGlobal;
		Coordinates = coordinates;
	}

	public bool IsGlobal { get; }
	public Int2 Coordinates { get; }

	public static SceneCellKey Global => new(true, Int2.Zero);
	public static SceneCellKey Spatial(Int2 coordinates) => new(false, coordinates);

	public bool Equals(SceneCellKey other) => IsGlobal == other.IsGlobal && Coordinates.Equals(other.Coordinates);

	public override bool Equals(object? obj) => obj is SceneCellKey other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(IsGlobal, Coordinates);

	public static bool operator ==(SceneCellKey left, SceneCellKey right) => left.Equals(right);
	public static bool operator !=(SceneCellKey left, SceneCellKey right) => left.Equals(right) == false;
}
