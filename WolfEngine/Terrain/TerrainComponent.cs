using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine;

public struct TerrainComponent : IEntityComponent
{
	public AssetRef<TerrainAsset> TerrainAsset;
	public AssetRef<TerrainLayerSet> LayerSetAsset;
	[JsonIgnore]
	public Material? Material;
	public Vector2 WorldSizeMeters;
	public float HeightScaleMeters;
	public float ChunkSizeMeters;
	public int LodCount;
	public int Lod0ResolutionInQuads;
	public int RayTracingResolutionInQuads;
	public float[] LodDistancesMeters;
	[HideFromEditor]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public int ChunkSizeInQuads;
	[NotSerialized]
	[HideFromEditor]
	internal bool PhysicsCacheValid;
	[NotSerialized]
	[HideFromEditor]
	internal int CachedRuntimeVersion;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedLayer;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedCollidesWith;
	[NotSerialized]
	[HideFromEditor]
	internal int CachedHeightfieldFailureVersion;
	[NotSerialized]
	[JsonIgnore]
	[HideFromEditor]
	public Texture? AuthoringPreviewHeightmap;
	[NotSerialized]
	[JsonIgnore]
	[HideFromEditor]
	public Texture? AuthoringPreviewLayerIndexMap;
	[NotSerialized]
	[JsonIgnore]
	[HideFromEditor]
	public Texture? AuthoringPreviewLayerWeightMap;
	[NotSerialized]
	[JsonIgnore]
	[HideFromEditor]
	public DecalProjector? AuthoringBrushPreviewDecal;
	[NotSerialized]
	[JsonIgnore]
	[HideFromEditor]
	public Matrix4x4 AuthoringBrushPreviewLocalTransform;

	public void ApplyDefaultValues(World world, Entity entity)
	{
		_ = world;
		_ = entity;
		WorldSizeMeters = new Vector2(512.0f, 512.0f);
		HeightScaleMeters = 64.0f;
		ChunkSizeMeters = 64.0f;
		LodCount = 3;
		Lod0ResolutionInQuads = 32;
		RayTracingResolutionInQuads = 16;
		LodDistancesMeters = [120.0f, 320.0f];
		ChunkSizeInQuads = 0;
		PhysicsCacheValid = false;
		CachedHeightfieldFailureVersion = -1;
		AuthoringPreviewHeightmap = null;
		AuthoringPreviewLayerIndexMap = null;
		AuthoringPreviewLayerWeightMap = null;
		AuthoringBrushPreviewDecal = null;
		AuthoringBrushPreviewLocalTransform = Matrix4x4.Identity;
	}

	public Vector2 GetResolvedWorldSize()
	{
		var width = WorldSizeMeters.X > 1.0f ? WorldSizeMeters.X : 512.0f;
		var height = WorldSizeMeters.Y > 1.0f ? WorldSizeMeters.Y : 512.0f;
		return new Vector2(width, height);
	}

	public float GetResolvedHeightScale() => HeightScaleMeters > 0.01f ? HeightScaleMeters : 64.0f;

	public float GetResolvedChunkSizeMeters()
	{
		var chunkSize = ChunkSizeMeters;
		if (chunkSize <= 0.01f)
		{
			chunkSize = 64.0f;
		}

		return Math.Max(1.0f, chunkSize);
	}

	public int GetResolvedLegacyChunkSizeInQuads()
	{
		var chunkSize = ChunkSizeInQuads;
		if (chunkSize < 4)
		{
			chunkSize = 64;
		}

		return Math.Max(4, chunkSize);
	}

	public int GetResolvedLodCount()
	{
		var count = LodCount;
		if (count < 1)
		{
			count = 3;
		}

		return Math.Clamp(count, 1, 8);
	}

	public int GetResolvedLod0ResolutionInQuads()
	{
		var resolution = Lod0ResolutionInQuads;
		if (resolution < 2)
		{
			resolution = 32;
		}

		return Math.Clamp(resolution, 2, 1024);
	}

	public int GetResolvedRayTracingResolutionInQuads()
	{
		var resolution = RayTracingResolutionInQuads;
		if (resolution < 1)
		{
			resolution = 16;
		}

		return Math.Clamp(resolution, 1, 256);
	}

	public float[] GetResolvedLodDistancesMeters()
	{
		var lodCount = GetResolvedLodCount();
		if (lodCount <= 1)
		{
			return Array.Empty<float>();
		}

		var source = LodDistancesMeters ?? Array.Empty<float>();
		var result = new float[lodCount - 1];
		var previous = 0.0f;
		for (var i = 0; i < result.Length; i++)
		{
			var fallback = GetDefaultLodDistance(i);
			var candidate = i < source.Length ? source[i] : fallback;
			if (candidate <= previous + 0.001f)
			{
				candidate = Math.Max(fallback, previous + 1.0f);
			}

			result[i] = candidate;
			previous = candidate;
		}

		return result;
	}

	private static float GetDefaultLodDistance(int index)
	{
		if (index <= 0)
		{
			return 120.0f;
		}

		var distance = 120.0f;
		var step = 200.0f;
		for (var i = 0; i < index; i++)
		{
			distance += step;
			step *= 2.0f;
		}

		return distance;
	}
}
