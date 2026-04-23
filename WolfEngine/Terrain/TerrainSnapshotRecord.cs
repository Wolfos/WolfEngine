using System;
using System.Collections.Generic;
using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine;

public readonly struct TerrainDrawSurface
{
	public TerrainDrawSurface(
		Texture? controlMap,
		int layerCount,
		float heightBlendSharpness,
		IReadOnlyList<TerrainResolvedLayer> layers)
	{
		ControlMap = controlMap;
		ControlMapResourceRevision = controlMap?.ResourceRevision ?? 0;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		Layers = layers ?? throw new ArgumentNullException(nameof(layers));
	}

	public Texture? ControlMap { get; }
	public int ControlMapResourceRevision { get; }
	public int LayerCount { get; }
	public float HeightBlendSharpness { get; }
	public IReadOnlyList<TerrainResolvedLayer> Layers { get; }
}

public readonly struct TerrainChunkDrawRecord
{
	public TerrainChunkDrawRecord(int chunkIndex, Mesh mesh, Material material, Matrix4x4 worldTransform, TerrainDrawSurface surface)
	{
		ChunkIndex = chunkIndex;
		Mesh = mesh;
		Material = material;
		WorldTransform = worldTransform;
		Surface = surface;
	}

	public int ChunkIndex { get; }
	public Mesh Mesh { get; }
	public Material Material { get; }
	public Matrix4x4 WorldTransform { get; }
	public TerrainDrawSurface Surface { get; }
}

public readonly struct TerrainResolvedLayer
{
	public TerrainResolvedLayer(Texture? albedo, Texture? normal, Texture? metallicRoughness, Texture? occlusion, Texture? height, float scale)
	{
		Albedo = albedo;
		AlbedoResourceRevision = albedo?.ResourceRevision ?? 0;
		Normal = normal;
		NormalResourceRevision = normal?.ResourceRevision ?? 0;
		MetallicRoughness = metallicRoughness;
		MetallicRoughnessResourceRevision = metallicRoughness?.ResourceRevision ?? 0;
		Occlusion = occlusion;
		OcclusionResourceRevision = occlusion?.ResourceRevision ?? 0;
		Height = height;
		HeightResourceRevision = height?.ResourceRevision ?? 0;
		Scale = scale;
	}

	public Texture? Albedo { get; }
	public int AlbedoResourceRevision { get; }
	public Texture? Normal { get; }
	public int NormalResourceRevision { get; }
	public Texture? MetallicRoughness { get; }
	public int MetallicRoughnessResourceRevision { get; }
	public Texture? Occlusion { get; }
	public int OcclusionResourceRevision { get; }
	public Texture? Height { get; }
	public int HeightResourceRevision { get; }
	public float Scale { get; }
}
