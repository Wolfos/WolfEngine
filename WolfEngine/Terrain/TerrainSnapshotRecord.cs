using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;

namespace WolfEngine;

public readonly struct TerrainDrawSurface
{
	public TerrainDrawSurface(
		Texture? heightmap,
		Texture? layerIndexMap,
		Texture? layerWeightMap,
		float heightScale,
		int layerCount,
		float heightBlendSharpness,
		float autoMaterialBlendDegrees,
		IReadOnlyList<TerrainResolvedLayer> layers)
	{
		Heightmap = heightmap;
		HeightmapResourceRevision = heightmap?.ResourceRevision ?? 0;
		LayerIndexMap = layerIndexMap;
		LayerIndexMapResourceRevision = layerIndexMap?.ResourceRevision ?? 0;
		LayerWeightMap = layerWeightMap;
		LayerWeightMapResourceRevision = layerWeightMap?.ResourceRevision ?? 0;
		HeightScale = heightScale;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		AutoMaterialBlendDegrees = autoMaterialBlendDegrees;
		Layers = layers ?? throw new ArgumentNullException(nameof(layers));
	}

	public Texture? Heightmap { get; }
	public int HeightmapResourceRevision { get; }
	public Texture? LayerIndexMap { get; }
	public int LayerIndexMapResourceRevision { get; }
	public Texture? LayerWeightMap { get; }
	public int LayerWeightMapResourceRevision { get; }
	public float HeightScale { get; }
	public int LayerCount { get; }
	public float HeightBlendSharpness { get; }
	public float AutoMaterialBlendDegrees { get; }
	public IReadOnlyList<TerrainResolvedLayer> Layers { get; }
}

public readonly struct TerrainChunkInstanceData
{
	public TerrainChunkInstanceData(Vector4 chunkOriginSize, Vector4 heightmapUvScaleOffset)
	{
		ChunkOriginSize = chunkOriginSize;
		HeightmapUvScaleOffset = heightmapUvScaleOffset;
	}

	public Vector4 ChunkOriginSize { get; }
	public Vector4 HeightmapUvScaleOffset { get; }
}

public readonly struct TerrainChunkDrawRecord
{
	public TerrainChunkDrawRecord(
		int chunkIndex,
		Mesh mesh,
		Material material,
		Matrix4x4 worldTransform,
		BoundingSphere localBounds,
		TerrainChunkInstanceData instanceData,
		TerrainDrawSurface surface,
		TerrainRayTracingChunkData rayTracingChunk)
	{
		ChunkIndex = chunkIndex;
		Mesh = mesh;
		Material = material;
		WorldTransform = worldTransform;
		LocalBounds = localBounds;
		InstanceData = instanceData;
		Surface = surface;
		RayTracingChunk = rayTracingChunk;
	}

	public int ChunkIndex { get; }
	public Mesh Mesh { get; }
	public Material Material { get; }
	public Matrix4x4 WorldTransform { get; }
	public BoundingSphere LocalBounds { get; }
	public TerrainChunkInstanceData InstanceData { get; }
	public TerrainDrawSurface Surface { get; }
	public TerrainRayTracingChunkData RayTracingChunk { get; }
}

public readonly struct TerrainRayTracingChunkData
{
	public TerrainRayTracingChunkData(
		int chunkIndex,
		int resolutionInQuads,
		int geometryRevision,
		Vector4 chunkOriginSize,
		Vector4 heightmapUvScaleOffset)
	{
		ChunkIndex = chunkIndex;
		ResolutionInQuads = resolutionInQuads;
		GeometryRevision = geometryRevision;
		ChunkOriginSize = chunkOriginSize;
		HeightmapUvScaleOffset = heightmapUvScaleOffset;
	}

	public int ChunkIndex { get; }
	public int ResolutionInQuads { get; }
	public int GeometryRevision { get; }
	public Vector4 ChunkOriginSize { get; }
	public Vector4 HeightmapUvScaleOffset { get; }
}

public readonly struct TerrainResolvedLayer
{
	public TerrainResolvedLayer(
		Texture? albedo,
		Texture? normal,
		Texture? orm,
		Texture? height,
		float scale,
		bool autoMaterial = false,
		bool useMinimumSlope = false,
		float minimumSlopeDegrees = 45.0f)
	{
		Albedo = albedo;
		AlbedoResourceRevision = albedo?.ResourceRevision ?? 0;
		Normal = normal;
		NormalResourceRevision = normal?.ResourceRevision ?? 0;
		Orm = orm;
		OrmResourceRevision = orm?.ResourceRevision ?? 0;
		Height = height;
		HeightResourceRevision = height?.ResourceRevision ?? 0;
		Scale = scale;
		AutoMaterial = autoMaterial;
		UseMinimumSlope = useMinimumSlope;
		MinimumSlopeDegrees = minimumSlopeDegrees;
	}

	public Texture? Albedo { get; }
	public int AlbedoResourceRevision { get; }
	public Texture? Normal { get; }
	public int NormalResourceRevision { get; }
	public Texture? Orm { get; }
	public int OrmResourceRevision { get; }
	public Texture? Height { get; }
	public int HeightResourceRevision { get; }
	public float Scale { get; }
	public bool AutoMaterial { get; }
	public bool UseMinimumSlope { get; }
	public float MinimumSlopeDegrees { get; }
}
