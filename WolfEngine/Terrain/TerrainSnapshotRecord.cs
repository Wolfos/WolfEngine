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
		Texture? controlMap,
		float heightScale,
		int layerCount,
		float heightBlendSharpness,
		IReadOnlyList<TerrainResolvedLayer> layers)
	{
		Heightmap = heightmap;
		HeightmapResourceRevision = heightmap?.ResourceRevision ?? 0;
		ControlMap = controlMap;
		ControlMapResourceRevision = controlMap?.ResourceRevision ?? 0;
		HeightScale = heightScale;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		Layers = layers ?? throw new ArgumentNullException(nameof(layers));
	}

	public Texture? Heightmap { get; }
	public int HeightmapResourceRevision { get; }
	public Texture? ControlMap { get; }
	public int ControlMapResourceRevision { get; }
	public float HeightScale { get; }
	public int LayerCount { get; }
	public float HeightBlendSharpness { get; }
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
		TerrainDrawSurface surface)
	{
		ChunkIndex = chunkIndex;
		Mesh = mesh;
		Material = material;
		WorldTransform = worldTransform;
		LocalBounds = localBounds;
		InstanceData = instanceData;
		Surface = surface;
	}

	public int ChunkIndex { get; }
	public Mesh Mesh { get; }
	public Material Material { get; }
	public Matrix4x4 WorldTransform { get; }
	public BoundingSphere LocalBounds { get; }
	public TerrainChunkInstanceData InstanceData { get; }
	public TerrainDrawSurface Surface { get; }
}

public readonly struct TerrainResolvedLayer
{
	public TerrainResolvedLayer(Texture? albedo, Texture? normal, Texture? orm, Texture? height, float scale)
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
}
