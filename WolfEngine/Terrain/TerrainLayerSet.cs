using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;

namespace WolfEngine;

[RuntimeAsset(AssetType.DataAsset, typeof(TerrainLayerSet), typeof(IDataAssetRuntimeResolver))]
public sealed class TerrainLayerSet : IDataAsset
{
	public int ActiveLayerCount { get; set; } = 2;
	public float HeightBlendSharpness { get; set; } = 4.0f;
	public TerrainLayerDefinition Layer0 { get; set; } = new();
	public TerrainLayerDefinition Layer1 { get; set; } = new();
	public TerrainLayerDefinition Layer2 { get; set; } = new();
	public TerrainLayerDefinition Layer3 { get; set; } = new();

	[JsonIgnore]
	public int ResolvedLayerCount => Math.Clamp(ActiveLayerCount, 1, 4);

	public IEnumerable<TerrainLayerDefinition> EnumerateActiveLayers()
	{
		yield return Layer0;
		if (ResolvedLayerCount > 1) yield return Layer1;
		if (ResolvedLayerCount > 2) yield return Layer2;
		if (ResolvedLayerCount > 3) yield return Layer3;
	}

	public TerrainLayerDefinition GetLayer(int index)
	{
		return index switch
		{
			0 => Layer0,
			1 => Layer1,
			2 => Layer2,
			3 => Layer3,
			_ => throw new ArgumentOutOfRangeException(nameof(index))
		};
	}
}

public sealed class TerrainLayerDefinition
{
	public AssetRef<Texture> Albedo { get; set; }
	public AssetRef<Texture> Normal { get; set; }
	public AssetRef<Texture> MetallicRoughness { get; set; }
	public AssetRef<Texture> Occlusion { get; set; }
	public AssetRef<Texture> Height { get; set; }
}
