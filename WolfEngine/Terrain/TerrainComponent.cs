using System;
using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine;

public struct TerrainComponent : IEntityComponent
{
	public AssetRef<Texture> HeightmapAsset;
	public AssetRef<Texture> ControlMapAsset;
	public AssetRef<TerrainLayerSet> LayerSetAsset;
	public Vector2 WorldSizeMeters;
	public float HeightScaleMeters;
	public int ChunkSizeInQuads;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		_ = world;
		_ = entity;
		WorldSizeMeters = new Vector2(512.0f, 512.0f);
		HeightScaleMeters = 64.0f;
		ChunkSizeInQuads = 64;
	}

	public Vector2 GetResolvedWorldSize()
	{
		var width = WorldSizeMeters.X > 1.0f ? WorldSizeMeters.X : 512.0f;
		var height = WorldSizeMeters.Y > 1.0f ? WorldSizeMeters.Y : 512.0f;
		return new Vector2(width, height);
	}

	public float GetResolvedHeightScale() => HeightScaleMeters > 0.01f ? HeightScaleMeters : 64.0f;

	public int GetResolvedChunkSizeInQuads()
	{
		var chunkSize = ChunkSizeInQuads;
		if (chunkSize < 4)
		{
			chunkSize = 4;
		}

		return Math.Max(4, chunkSize);
	}
}
