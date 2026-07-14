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

	[JsonInclude]
	public List<TerrainLayerDefinition> Layers { get; private set; } =
	[
		new(),
		new()
	];

	[JsonIgnore]
	public int ResolvedLayerCount
	{
		get
		{
			var requestedCount = Math.Max(ActiveLayerCount, 1);
			EnsureLayerCapacity(requestedCount);
			return requestedCount;
		}
	}

	public TerrainLayerDefinition GetLayer(int index)
	{
		if (index < 0 || index >= ResolvedLayerCount)
		{
			throw new ArgumentOutOfRangeException(nameof(index));
		}

		return Layers[index];
	}

	// Preserve the older serialized shape while migrating assets to the list-based form.
	[JsonInclude]
	public TerrainLayerDefinition Layer0
	{
		get => GetLegacyLayer(0);
		set => SetLegacyLayer(0, value);
	}

	[JsonInclude]
	public TerrainLayerDefinition Layer1
	{
		get => GetLegacyLayer(1);
		set => SetLegacyLayer(1, value);
	}

	[JsonInclude]
	public TerrainLayerDefinition Layer2
	{
		get => GetLegacyLayer(2);
		set => SetLegacyLayer(2, value);
	}

	[JsonInclude]
	public TerrainLayerDefinition Layer3
	{
		get => GetLegacyLayer(3);
		set => SetLegacyLayer(3, value);
	}

	public void EnsureLayerCapacity(int count)
	{
		if (count <= 0)
		{
			count = 1;
		}

		while (Layers.Count < count)
		{
			Layers.Add(new TerrainLayerDefinition());
		}
	}

	private TerrainLayerDefinition GetLegacyLayer(int index)
	{
		EnsureLayerCapacity(index + 1);
		return Layers[index];
	}

	private void SetLegacyLayer(int index, TerrainLayerDefinition value)
	{
		EnsureLayerCapacity(index + 1);
		Layers[index] = value ?? new TerrainLayerDefinition();
	}
}

public sealed class TerrainLayerDefinition
{
	public float Scale { get; set; } = 8.0f;
	public bool AutoMaterial { get; set; }
	public bool UseMinimumSlope { get; set; }
	public float MinimumSlopeDegrees { get; set; } = 45.0f;
	public AssetRef<Texture> Albedo { get; set; }
	public AssetRef<Texture> Normal { get; set; }
	public AssetRef<Texture> Orm { get; set; }
	public AssetRef<Texture> Height { get; set; }
}
