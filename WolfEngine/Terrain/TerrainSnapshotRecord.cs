using System.Numerics;
using WolfEngine.Rendering;

namespace WolfEngine;

public readonly struct TerrainDrawSurface
{
	public TerrainDrawSurface(
		Texture? controlMap,
		int layerCount,
		float heightBlendSharpness,
		TerrainResolvedLayer layer0,
		TerrainResolvedLayer layer1,
		TerrainResolvedLayer layer2,
		TerrainResolvedLayer layer3)
	{
		ControlMap = controlMap;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		Layer0 = layer0;
		Layer1 = layer1;
		Layer2 = layer2;
		Layer3 = layer3;
	}

	public Texture? ControlMap { get; }
	public int LayerCount { get; }
	public float HeightBlendSharpness { get; }
	public TerrainResolvedLayer Layer0 { get; }
	public TerrainResolvedLayer Layer1 { get; }
	public TerrainResolvedLayer Layer2 { get; }
	public TerrainResolvedLayer Layer3 { get; }
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
		Normal = normal;
		MetallicRoughness = metallicRoughness;
		Occlusion = occlusion;
		Height = height;
		Scale = scale;
	}

	public Texture? Albedo { get; }
	public Texture? Normal { get; }
	public Texture? MetallicRoughness { get; }
	public Texture? Occlusion { get; }
	public Texture? Height { get; }
	public float Scale { get; }
}
